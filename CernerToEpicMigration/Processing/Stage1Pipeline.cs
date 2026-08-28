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
    private readonly FileTraceWriter _traceWriter;
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
        FileTraceWriter traceWriter,
        CheckpointService checkpointService,
        ILogger<Stage1Pipeline> logger)
    {
        _config = config;
        _discovery = discovery;
        _fileManager = fileManager;
        _converter = converter;
        _metrics = metrics;
        _reportWriter = reportWriter;
        _traceWriter = traceWriter;
        _checkpointService = checkpointService;
        _logger = logger;
    }

    /// <summary>RTF output runs about 1.2x the size of the XHTML input (design document 12.3).</summary>
    private const double RtfSizeFactor = 1.2;

    /// <summary>A Base64 envelope turns every three bytes into four.</summary>
    private const double Base64SizeFactor = 4d / 3d;

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

        if (options.Resume && !_config.Processing.ArchiveOnSuccess)
        {
            _logger.LogInformation(
                "Resume with ArchiveOnSuccess=false: converted documents stay in the input folder, so an " +
                "interrupted date folder is resumed by skipping the documents that already have an RTF.");
        }

        if (folders.Count == 0)
        {
            _logger.LogWarning("No date folders with input files found under {Path}.", _config.InputBasePath);
            return new Stage1Result { Completed = true, Folders = Array.Empty<FolderStatistics>() };
        }

        IReadOnlyDictionary<string, int> fileCounts = PreScan(folders);

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

            bool folderCompleted = await ProcessFolderAsync(
                    folder,
                    fileCounts.TryGetValue(folder.Name, out int count) ? count : UnknownFileCount,
                    options.Resume,
                    checkpoint,
                    runCts)
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

    /// <summary>Marks a folder whose file count was never established, because the pre-scan was off.</summary>
    private const int UnknownFileCount = -1;

    /// <summary>
    /// Counts every input file before conversion starts so the progress bar and the
    /// remaining-time estimate cover the whole run. Returns an empty map when the pre-scan is
    /// switched off, in which case each folder is counted as the run reaches it.
    /// </summary>
    private IReadOnlyDictionary<string, int> PreScan(IReadOnlyList<DateFolder> folders)
    {
        if (!_config.Processing.PreScanForEstimates)
        {
            _logger.LogInformation(
                "Pre-scan disabled: {Folders} date folder(s) to process, each counted as the run reaches it. " +
                "Progress totals grow folder by folder and the disk-space check is skipped. " +
                "Parallelism={Threads}, BatchSize={BatchSize}.",
                folders.Count, _config.Processing.EffectiveParallelism, _config.Processing.BatchSize);

            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

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
        return fileCounts;
    }

    /// <summary>Processes one date folder batch by batch. Returns false if the run was stopped.</summary>
    private async Task<bool> ProcessFolderAsync(
        DateFolder folder, int fileCount, bool resume, Checkpoint checkpoint, CancellationTokenSource runCts)
    {
        _metrics.SetCurrentFolder(folder.Name);

        // The list is materialised before any file is touched: processing moves files out of
        // this folder, and a lazy directory enumeration can skip entries while that happens.
        string[] files = _discovery.EnumerateFiles(folder).ToArray();

        if (fileCount == UnknownFileCount)
        {
            // No pre-scan, so this is the first time the folder has been counted.
            _metrics.RegisterFolder(folder.Name, files.Length);
        }
        else if (files.Length != fileCount)
        {
            _logger.LogWarning(
                "Folder {Folder} holds {Actual:N0} file(s) at processing time; the initial scan counted {Expected:N0}.",
                folder.Name, files.Length, fileCount);
            _metrics.AdjustFolderTotal(folder.Name, files.Length - fileCount);
        }

        files = SkipAlreadyConverted(folder, files, resume);

        QuarantineUnmatchedFiles(folder);

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

            // Stamped once per batch and carried on every work item, so a row of the error report
            // names the batch it came from and the run log line for that batch gives its timing.
            string batchId = WorkItem.FormatBatchId(folder.Name, batchNumber);

            try
            {
                await Parallel.ForEachAsync(
                    batch,
                    parallelOptions,
                    (filePath, token) => ProcessFileAsync(new WorkItem(filePath, folder, batchId), token))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                _logger.LogWarning(
                    exception, "Batch {Batch} of folder {Folder} was interrupted.", batchNumber, folder.Name);
                break;
            }

            // A batch boundary is where the run becomes durable, so the error report is brought
            // up to date with the checkpoint rather than being left in the write buffer.
            _reportWriter.Flush();
            _traceWriter.Flush();

            checkpoint.LastProcessedFolder = folder.Name;
            checkpoint.LastProcessedFile = Path.GetFileName(batch[^1]);
            checkpoint.TotalSucceeded = _metrics.BaselineSucceeded + _metrics.Succeeded;
            checkpoint.TotalFailed = _metrics.BaselineFailed + _metrics.Failed;
            checkpoint.TotalProcessed = checkpoint.TotalSucceeded + checkpoint.TotalFailed;
            _checkpointService.Save(checkpoint);

            // The dashboard is not there on an unattended run, so progress goes to the log too.
            _logger.LogInformation(
                "Batch {BatchId} ({Batch}/{Batches}) done - {Succeeded:N0} succeeded, {Failed:N0} failed overall, {Rate:N1} files/s.",
                batchId, batchNumber, totalBatches, _metrics.Succeeded, _metrics.Failed, _metrics.FilesPerSecond);
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
    /// Drops the documents of a partly finished folder that a previous run already converted.
    /// </summary>
    /// <remarks>
    /// With <c>ArchiveOnSuccess</c> on - the default - converted documents have already left the
    /// input folder, so re-enumerating it is the resume: nothing here matches and the check is
    /// skipped. With archiving off they stay put, and without this a run killed 900k documents
    /// into a folder would convert all 900k again. The completed RTF is the marker because it is
    /// written to a temp file and renamed, so its presence means a finished conversion; the cost
    /// is one existence check per remaining file, paid only on a resume.
    /// </remarks>
    private string[] SkipAlreadyConverted(DateFolder folder, string[] files, bool resume)
    {
        if (!resume || _config.Processing.ArchiveOnSuccess || files.Length == 0)
            return files;

        string[] remaining = files
            .Where(filePath => !File.Exists(_fileManager.GetRtfOutputPath(new WorkItem(filePath, folder))))
            .ToArray();

        int skipped = files.Length - remaining.Length;
        if (skipped == 0)
            return files;

        _logger.LogInformation(
            "Resume: {Skipped:N0} of {Total:N0} document(s) in {Folder} already have an RTF and are skipped.",
            skipped, files.Length, folder.Name);

        _metrics.AdjustFolderTotal(folder.Name, -skipped);
        return remaining;
    }

    /// <summary>
    /// Converts one document, retrying transient failures per section 10.2 and moving the
    /// file to the error folder once the attempts are exhausted.
    /// </summary>
    private async ValueTask ProcessFileAsync(WorkItem item, CancellationToken cancellationToken)
    {
        // The slot is this worker's identity for as long as it holds this document. The managed
        // thread id is recorded too, but it is not the identity: the continuation after the retry
        // delay below can come back on a different pool thread.
        int slot = _metrics.WorkerStarted();
        int threadId = Environment.CurrentManagedThreadId;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long startTicks = Stopwatch.GetTimestamp();

        string outcomeLabel = "Cancelled";
        int attempts = 0;

        try
        {
            Exception? lastError = null;
            ErrorCategory category = ErrorCategory.Permanent;

            while (attempts < _config.Processing.MaxRetryCount)
            {
                attempts++;

                try
                {
                    string outputPath = _fileManager.GetRtfOutputPath(item);
                    ConversionOutcome outcome = _converter.Convert(item.FilePath, outputPath);
                    _fileManager.ArchiveSuccess(item);
                    _metrics.RecordSuccess(item.Folder.Name, outcome.InputBytes, outcome.OutputBytes);

                    outcomeLabel = "Succeeded";
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
                        outcomeLabel = "Fatal";
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

            outcomeLabel = $"Failed:{category}";
            FailFile(item, lastError!, category, attempts);
        }
        finally
        {
            TimeSpan held = Stopwatch.GetElapsedTime(startTicks);
            _metrics.WorkerFinished(slot, held);

            // Recorded on every path, including the cancelled one - a trace that silently drops
            // the files that were in flight when the run stopped would understate concurrency at
            // exactly the moment it matters.
            _traceWriter.Record(new FileTrace(
                WorkerSlot: slot,
                ThreadId: threadId,
                DateFolder: item.Folder.Name,
                BatchId: item.BatchId,
                FileName: Path.GetFileName(item.FilePath),
                SubFolder: item.SubFolder,
                StartUtc: startedAt,
                Duration: held,
                Attempts: attempts,
                Outcome: outcomeLabel));
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
            StackTrace: exception.StackTrace,
            BatchId: item.BatchId,
            SubFolder: item.SubFolder,
            Source: FailureSource.Conversion,
            Reason: DescribeReason(exception, category),
            FileSizeBytes: FileSizeOrZero(item.FilePath));

        _logger.LogError(
            exception,
            "{Category} failure after {Attempts} attempt(s) for {File} in batch {BatchId}; moving to the error folder.",
            category, attempts, item.FilePath, item.BatchId);

        RecordFailure(item, failure);
        _metrics.RecordFailure(item.Folder.Name);
    }

    /// <summary>
    /// Files one failure: the document into the error folder, the row into the error report. The
    /// placement is folded back into the failure first, because the report has to name the error
    /// log and the final location and neither is known until the move has been attempted.
    /// </summary>
    private void RecordFailure(WorkItem item, FileFailure failure)
    {
        try
        {
            ErrorPlacement placement = _fileManager.MoveToError(item, failure);

            failure = failure with
            {
                ErrorLogFileName = placement.ErrorLogFileName,
                ErrorFilePath = placement.ErrorFilePath,
                MoveError = placement.MoveError
            };

            if (placement.MoveError is not null)
            {
                _logger.LogWarning(
                    "{File} stays in the input folder because it could not be moved to the error folder: {Reason}",
                    item.FilePath, placement.MoveError);
            }
        }
        catch (Exception moveException)
        {
            // The failure is still recorded; the file simply stays put.
            _logger.LogError(moveException, "Could not write the error record for {File}.", item.FilePath);
            failure = failure with { MoveError = moveException.Message };
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
    }

    /// <summary>
    /// Moves every file the search pattern does not match into the error folder, with one row of
    /// the error report each.
    /// </summary>
    /// <remarks>
    /// These files used to be invisible. Discovery filters on
    /// <c>Processing.FileSearchPattern</c>, so a <c>.pdf</c>, a mistyped <c>.xhtm</c> or a
    /// half-renamed <c>.xhtml.bak</c> was never counted, never converted and never reported - and
    /// the date folder still finished clean, with documents sitting in it that nothing had looked
    /// at. Quarantining them makes them countable: they are added to the folder total before they
    /// are failed, so the summary arithmetic still holds (found = succeeded + failed) and the
    /// report says exactly which files they were and why they were rejected.
    /// <para>
    /// It runs before the batches, so the folder totals are right before the first progress line,
    /// and the rows carry <see cref="WorkItem.ScanBatchId"/> rather than a batch number - these
    /// documents never belonged to a batch.
    /// </para>
    /// </remarks>
    private void QuarantineUnmatchedFiles(DateFolder folder)
    {
        if (!_config.Processing.QuarantineUnmatchedFiles)
            return;

        string[] unmatched;
        try
        {
            unmatched = _discovery.EnumerateUnmatchedFiles(folder).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Never let the tidy-up sweep stop the conversion it runs in front of.
            _logger.LogWarning(exception, "Could not list the unmatched files of {Folder}.", folder.Name);
            return;
        }

        if (unmatched.Length == 0)
            return;

        _logger.LogWarning(
            "{Count:N0} file(s) in {Folder} do not match the search pattern {Pattern}; moving them to the error folder.",
            unmatched.Length, folder.Name, _config.Processing.FileSearchPattern);

        // Counted in before they are failed, so processed never exceeds found.
        _metrics.RegisterFolder(folder.Name, unmatched.Length);

        foreach (string filePath in unmatched)
        {
            WorkItem item = new(filePath, folder);
            string fileName = Path.GetFileName(filePath);
            string pattern = _config.Processing.FileSearchPattern;

            FileFailure failure = new(
                FilePath: Path.GetFullPath(filePath),
                DateFolder: folder.Name,
                Category: ErrorCategory.Permanent,
                ErrorType: PatternMismatchErrorType,
                ErrorMessage:
                    $"{fileName} does not match the configured search pattern {pattern}, " +
                    "so it was never a conversion candidate.",
                Attempts: 0,
                TimestampUtc: DateTimeOffset.UtcNow,
                StackTrace: null,
                BatchId: WorkItem.ScanBatchId,
                SubFolder: item.SubFolder,
                Source: FailureSource.Discovery,
                Reason: $"File name does not match {pattern}",
                FileSizeBytes: FileSizeOrZero(filePath));

            RecordFailure(item, failure);
            _metrics.RecordFailure(folder.Name);
        }
    }

    /// <summary>
    /// Stands in for an exception type name in the error report, so the Error Type column groups
    /// pattern rejections the way it groups everything else.
    /// </summary>
    private const string PatternMismatchErrorType = "FileSearchPatternMismatch";

    /// <summary>
    /// Plain-language reason for a failure. The exception type and message are in the report too,
    /// but neither groups well: an operator scanning tens of thousands of rows needs a column with
    /// a handful of distinct values in it to see that 9,000 of them are the same problem.
    /// </summary>
    private static string DescribeReason(Exception exception, ErrorCategory category) => exception switch
    {
        NotXhtmlContentException => "The file holds no XHTML document",
        Base64DecodingException => "The file is not a decodable Base64 envelope",
        PathTooLongException => "Path is longer than the file system allows",
        FileNotFoundException => "The file disappeared before it could be read",
        DirectoryNotFoundException => "The folder disappeared before the file could be read",
        TimeoutException => "Conversion exceeded ConversionTimeoutSeconds",
        UnauthorizedAccessException => "Access denied to the file or the output folder",
        OutOfMemoryException => "Not enough memory to convert the document",
        OperationCanceledException => "Stopped while the document was being converted",
        IOException when category == ErrorCategory.Fatal => "The output volume is full or unreachable",
        IOException => "The file is locked or the volume is unavailable",
        _ => $"Conversion failed ({exception.GetType().Name})"
    };

    /// <summary>Size on disk, or 0 when it cannot be read - never a reason to fail a failure.</summary>
    private static long FileSizeOrZero(string filePath)
    {
        try
        {
            FileInfo info = new(filePath);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
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
        double sizeFactor = _config.Processing.EncodeRtfOutputAsBase64
            ? RtfSizeFactor * Base64SizeFactor
            : RtfSizeFactor;

        long required = (long)(inputBytes * sizeFactor);

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
