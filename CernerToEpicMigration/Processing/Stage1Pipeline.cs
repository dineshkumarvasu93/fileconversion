using System.Diagnostics;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Monitoring;
using CernerToEpicMigration.Reporting;
using CernerToEpicMigration.State;
using Microsoft.Extensions.Logging;

namespace CernerToEpicMigration.Processing;

/// <summary>Run-scoped switches that come from the command line rather than configuration.</summary>
public sealed record RunOptions(string? DateFolder, bool Resume);

/// <summary>
/// Stage 1 orchestrator: discovery -> batching -> parallel conversion -> archive/error
/// -> checkpoint (design document sections 8.1, 9 and 10).
/// </summary>
public sealed class Stage1Pipeline
{
    private readonly MigrationConfig _config;
    private readonly FileDiscoveryService _discovery;
    private readonly FileManager _fileManager;
    private readonly IXhtmlToRtfConverter _converter;
    private readonly MetricsCollector _metrics;
    private readonly ReportWriter _reportWriter;
    private readonly CheckpointService _checkpointService;
    private readonly ILogger<Stage1Pipeline> _logger;

    private CancellationTokenSource? _runCts;
    private Exception? _fatalError;

    public Stage1Pipeline(
        MigrationConfig config,
        FileDiscoveryService discovery,
        FileManager fileManager,
        IXhtmlToRtfConverter converter,
        MetricsCollector metrics,
        ReportWriter reportWriter,
        CheckpointService checkpointService,
        ILogger<Stage1Pipeline> logger)
    {
        _config = config;
        _discovery = discovery;
        _fileManager = fileManager;
        _converter = converter;
        _metrics = metrics;
        _reportWriter = reportWriter;
        _checkpointService = checkpointService;
        _logger = logger;
    }

    /// <summary>RTF output runs about 1.2x the size of the XHTML input (design document 12.3).</summary>
    private const double RtfSizeFactor = 1.2;

    /// <summary>--dry-run: report what would be processed without touching anything.</summary>
    public IReadOnlyList<(DateFolder Folder, FolderScan Scan)> Scan(string? dateFolder) =>
        _discovery.DiscoverFolders(dateFolder)
            .Select(folder => (folder, _discovery.Scan(folder)))
            .ToList();

    public async Task<Stage1Result> RunAsync(RunOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<DateFolder> folders = _discovery.DiscoverFolders(options.DateFolder);

        Checkpoint checkpoint;
        if (options.Resume)
        {
            checkpoint = _checkpointService.Load();
        }
        else
        {
            _checkpointService.ArchiveExisting();
            checkpoint = new Checkpoint();
        }

        if (options.Resume && checkpoint.CompletedFolders.Count > 0)
        {
            HashSet<string> completed = new(checkpoint.CompletedFolders, StringComparer.OrdinalIgnoreCase);
            int before = folders.Count;
            folders = folders.Where(folder => !completed.Contains(folder.Name)).ToList();
            _logger.LogInformation("Resume: skipping {Count} completed date folder(s).", before - folders.Count);

            _metrics.BaselineSucceeded = checkpoint.TotalSucceeded;
            _metrics.BaselineFailed = checkpoint.TotalFailed;
        }

        if (folders.Count == 0)
        {
            _logger.LogWarning("No date folders with input files found under {Path}.", _config.InputBasePath);
            return new Stage1Result { Completed = true, Folders = Array.Empty<FolderStatistics>() };
        }

        // Counting up front is one extra directory scan, but it is what makes the
        // progress bar and the remaining-time estimate meaningful.
        Stopwatch scanTimer = Stopwatch.StartNew();
        Dictionary<string, int> fileCounts = new(StringComparer.Ordinal);
        long inputBytes = 0;
        foreach (DateFolder folder in folders)
        {
            FolderScan scan = _discovery.Scan(folder);
            fileCounts[folder.Name] = scan.Files;
            inputBytes += scan.Bytes;
            _metrics.RegisterFolder(folder.Name, scan.Files);
        }

        scanTimer.Stop();
        _logger.LogInformation(
            "Discovered {Files:N0} file(s) ({Gigabytes:N1} GB) across {Folders} date folder(s) in {Elapsed:N1}s. " +
            "Parallelism={Threads}, BatchSize={BatchSize}.",
            _metrics.TotalFound, AsGigabytes(inputBytes), folders.Count, scanTimer.Elapsed.TotalSeconds,
            _config.Processing.EffectiveParallelism, _config.Processing.BatchSize);

        WarnIfDiskSpaceIsShort(inputBytes);

        using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = runCts;
        _metrics.Start();

        bool cancelled = false;

        foreach (DateFolder folder in folders)
        {
            if (runCts.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            bool folderCompleted = await ProcessFolderAsync(folder, fileCounts[folder.Name], checkpoint, runCts)
                .ConfigureAwait(false);

            if (folderCompleted)
            {
                checkpoint.CompletedFolders.Add(folder.Name);
                _checkpointService.Save(checkpoint);
            }
            else
            {
                cancelled = true;
                break;
            }
        }

        _metrics.Stop();

        return new Stage1Result
        {
            Completed = !cancelled && _fatalError is null,
            Cancelled = cancelled && _fatalError is null,
            FatalError = _fatalError,
            Folders = _metrics.GetFolderStatistics()
        };
    }

    /// <summary>Processes one date folder batch by batch. Returns false if the run was stopped.</summary>
    private async Task<bool> ProcessFolderAsync(
        DateFolder folder, int fileCount, Checkpoint checkpoint, CancellationTokenSource runCts)
    {
        _metrics.SetCurrentFolder(folder.Name);

        // The list is materialised before any file is touched: processing moves files out of
        // this folder, and a lazy directory enumeration can skip entries while that happens.
        string[] files = _discovery.EnumerateFiles(folder).ToArray();

        if (files.Length != fileCount)
        {
            _logger.LogWarning(
                "Folder {Folder} holds {Actual:N0} file(s) at processing time; the initial scan counted {Expected:N0}.",
                folder.Name, files.Length, fileCount);
            _metrics.AdjustFolderTotal(folder.Name, files.Length - fileCount);
        }

        _logger.LogInformation("Processing date folder {Folder} ({Files:N0} file(s)).", folder.Name, files.Length);

        Directory.CreateDirectory(Path.Combine(Path.GetFullPath(_config.OutputRtfBasePath), folder.Name));

        int batchSize = _config.Processing.BatchSize;
        int totalBatches = Math.Max(1, (int)Math.Ceiling(files.Length / (double)batchSize));
        int batchNumber = 0;

        Stopwatch folderTimer = Stopwatch.StartNew();

        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = _config.Processing.EffectiveParallelism,
            CancellationToken = runCts.Token
        };

        foreach (string[] batch in files.Chunk(batchSize))
        {
            if (runCts.IsCancellationRequested)
                break;

            batchNumber++;
            _metrics.SetBatch(batchNumber, totalBatches);

            try
            {
                await Parallel.ForEachAsync(
                    batch,
                    parallelOptions,
                    (filePath, token) => ProcessFileAsync(new WorkItem(filePath, folder), token))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                _logger.LogWarning(
                    exception, "Batch {Batch} of folder {Folder} was interrupted.", batchNumber, folder.Name);
                break;
            }

            checkpoint.LastProcessedFolder = folder.Name;
            checkpoint.LastProcessedFile = Path.GetFileName(batch[^1]);
            checkpoint.TotalSucceeded = _metrics.BaselineSucceeded + _metrics.Succeeded;
            checkpoint.TotalFailed = _metrics.BaselineFailed + _metrics.Failed;
            checkpoint.TotalProcessed = checkpoint.TotalSucceeded + checkpoint.TotalFailed;
            _checkpointService.Save(checkpoint);

            // The dashboard is not there on an unattended run, so progress goes to the log too.
            _logger.LogInformation(
                "{Folder} batch {Batch}/{Batches} done - {Succeeded:N0} succeeded, {Failed:N0} failed overall, {Rate:N1} files/s.",
                folder.Name, batchNumber, totalBatches, _metrics.Succeeded, _metrics.Failed, _metrics.FilesPerSecond);
        }

        folderTimer.Stop();
        _metrics.SetFolderElapsed(folder.Name, folderTimer.Elapsed);

        bool completed = !runCts.IsCancellationRequested;
        if (completed)
        {
            _logger.LogInformation(
                "Finished date folder {Folder} in {Elapsed:hh\\:mm\\:ss}.", folder.Name, folderTimer.Elapsed);
        }

        return completed;
    }

    /// <summary>
    /// Converts one document, retrying transient failures per section 10.2 and moving the
    /// file to the error folder once the attempts are exhausted.
    /// </summary>
    private async ValueTask ProcessFileAsync(WorkItem item, CancellationToken cancellationToken)
    {
        _metrics.WorkerStarted();

        try
        {
            Exception? lastError = null;
            ErrorCategory category = ErrorCategory.Permanent;
            int attempts = 0;

            while (attempts < _config.Processing.MaxRetryCount)
            {
                attempts++;

                try
                {
                    string outputPath = _fileManager.GetRtfOutputPath(item);
                    ConversionOutcome outcome = _converter.Convert(item.FilePath, outputPath);
                    _fileManager.ArchiveSuccess(item);
                    _metrics.RecordSuccess(item.Folder.Name, outcome.InputBytes, outcome.OutputBytes);

                    _logger.LogDebug(
                        "Converted {File} ({InputBytes} bytes) to {Output} ({OutputBytes} bytes) on attempt {Attempt}.",
                        item.FilePath, outcome.InputBytes, outputPath, outcome.OutputBytes, attempts);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    category = ErrorClassifier.Classify(exception);

                    if (category == ErrorCategory.Fatal)
                    {
                        RaiseFatal(item, exception);
                        return;
                    }

                    if (category == ErrorCategory.Permanent || attempts >= _config.Processing.MaxRetryCount)
                        break;

                    _metrics.RecordRetry();
                    _logger.LogWarning(
                        exception,
                        "Attempt {Attempt}/{MaxAttempts} failed for {File}; retrying.",
                        attempts, _config.Processing.MaxRetryCount, item.FilePath);

                    await Task.Delay(_config.Processing.RetryDelayMs * attempts, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            FailFile(item, lastError!, category, attempts);
        }
        finally
        {
            _metrics.WorkerFinished();
        }
    }

    private void FailFile(WorkItem item, Exception exception, ErrorCategory category, int attempts)
    {
        FileFailure failure = new(
            FilePath: Path.GetFullPath(item.FilePath),
            DateFolder: item.Folder.Name,
            Category: category,
            ErrorType: exception.GetType().Name,
            ErrorMessage: exception.Message,
            Attempts: attempts,
            TimestampUtc: DateTimeOffset.UtcNow,
            StackTrace: exception.StackTrace);

        _logger.LogError(
            exception,
            "{Category} failure after {Attempts} attempt(s) for {File}; moving to the error folder.",
            category, attempts, item.FilePath);

        try
        {
            string? moveError = _fileManager.MoveToError(item, failure);
            if (moveError is not null)
            {
                _logger.LogWarning(
                    "{File} stays in the input folder because it could not be moved to the error folder: {Reason}",
                    item.FilePath, moveError);
            }
        }
        catch (Exception moveException)
        {
            // The conversion failure is still recorded; the file simply stays put.
            _logger.LogError(moveException, "Could not write the error record for {File}.", item.FilePath);
        }

        try
        {
            _reportWriter.RecordFailure(failure);
        }
        catch (Exception reportException)
        {
            // The error report is a convenience; the log and the error folder are the record
            // of truth, and neither is worth aborting a batch for.
            _logger.LogError(reportException, "The failure of {File} could not be added to the error report.", item.FilePath);
        }

        _metrics.RecordFailure(item.Folder.Name);
    }

    private void RaiseFatal(WorkItem item, Exception exception)
    {
        if (Interlocked.CompareExchange(ref _fatalError, exception, null) is null)
        {
            _logger.LogCritical(
                exception,
                "Fatal error while processing {File}; stopping the run. Input files are left in place.",
                item.FilePath);
        }

        // Stop the in-flight batch: a fatal error (disk full, permissions, licensing)
        // would fail every remaining file the same way.
        _runCts?.Cancel();
    }

    /// <summary>Set once a fatal error has been raised; the run stops as soon as it is seen.</summary>
    public Exception? FatalError => _fatalError;

    /// <summary>
    /// Advisory check only: the operator decides whether to go ahead. A volume that does fill
    /// up mid-run is still caught as a fatal error and stops processing cleanly.
    /// </summary>
    private void WarnIfDiskSpaceIsShort(long inputBytes)
    {
        long required = (long)(inputBytes * RtfSizeFactor);

        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(_config.OutputRtfBasePath))!);
            long available = drive.AvailableFreeSpace;

            _logger.LogInformation(
                "Capacity: about {Required:N1} GB of RTF expected, {Available:N1} GB free on {Drive}.",
                AsGigabytes(required), AsGigabytes(available), drive.Name);

            if (available >= required)
                return;

            string message =
                $"only {AsGigabytes(available):N1} GB free on {drive.Name}, but about " +
                $"{AsGigabytes(required):N1} GB of RTF output is expected";

            _logger.LogWarning("Disk space warning: {Message}.", message);
            Console.WriteLine($"  WARNING: {message}.");
            Console.WriteLine();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Free space is a nice-to-have; never block a run because it could not be read.
            _logger.LogWarning(exception, "Free disk space could not be determined for the output path.");
        }
    }

    private static double AsGigabytes(long bytes) => bytes / (1024d * 1024d * 1024d);
}
