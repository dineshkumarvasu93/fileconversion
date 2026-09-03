using System.Diagnostics;
using System.Globalization;
using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Monitoring;
using CernerToEpicMigration.Processing;
using Microsoft.Extensions.Logging;

namespace CernerToEpicMigration.Reporting;

/// <summary>
/// File-based reporting from design document sections 11.2 and 11.3.
/// Error rows are streamed as they happen so a run that is killed mid-way still
/// leaves a usable error report; the summary is written once at the end.
/// </summary>
/// <remarks>
/// The error report is the answer to "which documents failed, and where are they now?". Failed
/// documents are scattered across one <c>error</c> folder per date folder, so once there are
/// thousands of them the folders themselves are not reviewable - this report is the single list
/// that names every one of them, why it failed, which batch it was in, and the log file sitting
/// beside it. It rolls into numbered parts (see <see cref="RollingCsvWriter"/>) so it stays
/// openable on a run that fails millions of documents.
/// </remarks>
public sealed class ReportWriter : IDisposable
{
    /// <summary>Rows buffered before a flush is forced, however recent the last one was.</summary>
    private const int FlushRowThreshold = 500;

    /// <summary>How stale the on-disk error report is allowed to get while a run is going.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Columns of the error report. Ordered for triage: what failed, where it came from, why, and
    /// where to find it now - so the columns a reviewer sorts and filters on come first and the
    /// long free text sits out at the right where it does not push everything off screen.
    /// </summary>
    private const string ErrorReportHeader =
        "Row,Error Time Utc,Batch Id,Date Folder,Sub Folder,File Name,Actual File Name,File Size Bytes," +
        "Source,Error Category,Reason,Attempts,Error Log File,Moved To Error,Error File Path," +
        "Input File Path,Error Type,Error Message,Move Error";

    private readonly MigrationConfig _config;
    private readonly ILogger<ReportWriter> _logger;
    private readonly string _timestamp;
    private readonly object _errorLock = new();
    private readonly RollingCsvWriter _errorReport;

    private long _errorRowNumber;

    public ReportWriter(MigrationConfig config, ILogger<ReportWriter> logger)
    {
        _config = config;
        _logger = logger;
        _timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        _errorReport = new RollingCsvWriter(
            config.ReportBasePath,
            $"error_report_{_timestamp}",
            ErrorReportHeader,
            config.Processing.MaxReportRowsPerFile,
            FlushRowThreshold,
            FlushInterval);
    }

    /// <summary>
    /// Run stamp shared by every file this run produces, so the summary, the error report and the
    /// file trace can be matched up by name.
    /// </summary>
    public string Timestamp => _timestamp;

    public string SummaryReportPath =>
        Path.Combine(Path.GetFullPath(_config.ReportBasePath), $"migration_report_{_timestamp}.csv");

    /// <summary>
    /// First part of the error report. Known before anything fails, so it can be printed and
    /// probed even on a clean run; later parts are in <see cref="ErrorReportPaths"/>.
    /// </summary>
    public string ErrorReportPath => _errorReport.FirstPartPath;

    /// <summary>Every error report part written so far, in order.</summary>
    public IReadOnlyList<string> ErrorReportPaths => _errorReport.Parts;

    /// <summary>Failure rows written across every part.</summary>
    public long ErrorRowCount => _errorReport.RowCount;

    /// <summary>True once at least one failure row has been written.</summary>
    public bool HasErrorReport => _errorReport.HasRows;

    /// <summary>True once the end-of-run summary has been written to disk.</summary>
    public bool HasSummaryReport { get; private set; }

    /// <summary>Appends one row to the error report. Safe to call from worker threads.</summary>
    public void RecordFailure(FileFailure failure)
    {
        lock (_errorLock)
        {
            try
            {
                _errorReport.Write(
                    (++_errorRowNumber).ToString(CultureInfo.InvariantCulture),
                    failure.TimestampUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    failure.BatchId,
                    failure.DateFolder,
                    failure.SubFolder,
                    failure.FileName,
                    failure.ActualFileName,
                    failure.FileSizeBytes.ToString(CultureInfo.InvariantCulture),
                    failure.Source.ToString(),
                    failure.Category.ToString(),
                    failure.ReasonText,
                    failure.Attempts.ToString(CultureInfo.InvariantCulture),
                    failure.ErrorLogFileName ?? "(none)",
                    failure.MovedToError ? "Yes" : "No",
                    failure.ErrorFilePath ?? string.Empty,
                    failure.FilePath,
                    failure.ErrorType,
                    failure.ErrorMessage,
                    failure.MoveError ?? string.Empty);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The failure of {File} could not be added to the error report.", failure.FilePath);
            }
        }
    }

    /// <summary>
    /// Writes any buffered error rows to disk. Called on a threshold and an interval while a run
    /// is going, and on <see cref="Dispose"/>; call it directly when the file has to be current.
    /// </summary>
    public void Flush()
    {
        lock (_errorLock)
        {
            try
            {
                _errorReport.Flush();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The error report could not be flushed to disk.");
            }
        }
    }

    /// <summary>Writes the end-of-run summary CSV, including the per-folder breakdown.</summary>
    public void WriteSummary(MetricsCollector metrics, string status)
    {
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(_config.ReportBasePath));

            double filesPerSecond = metrics.FilesPerSecond;
            long totalBytes = metrics.BytesRead;
            long processed = metrics.Processed;

            StringBuilder report = new();
            report.AppendLine($"Report Generated,{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
            report.AppendLine($"Stage,1 (XHTML to RTF)");
            report.AppendLine($"Status,{Escape(status)}");
            report.AppendLine($"Input Path,{Escape(Path.GetFullPath(_config.InputBasePath))}");
            report.AppendLine($"Output Path,{Escape(Path.GetFullPath(_config.OutputRtfBasePath))}");
            report.AppendLine($"Threads,{_config.Processing.EffectiveParallelism}");
            // Configured vs observed. Peak must never exceed Threads and should reach it; the average
            // is what says whether those workers stayed busy or spent the run waiting. See
            // MetricsCollector.AverageConcurrency.
            report.AppendLine($"Peak Concurrent Workers,{metrics.PeakActiveWorkers}");
            report.AppendLine(
                $"Average Concurrent Workers,{metrics.AverageConcurrency.ToString("F2", CultureInfo.InvariantCulture)}");
            report.AppendLine(
                $"Worker Utilisation (%),{WorkerUtilisation(metrics).ToString("F1", CultureInfo.InvariantCulture)}");
            // The scaling test. Files per second says how fast the run was; this says how fast one
            // worker was, which is the part that should not change when workers are added. Compare
            // it across runs at 1, 2 and 4 threads: flat means the threads are buying throughput,
            // rising in step with the thread count means they are queueing for the same resource.
            report.AppendLine(
                $"Average Worker Service Time (ms),{metrics.AverageServiceTimeMs.ToString("F1", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Batch Size,{_config.Processing.BatchSize}");
            report.AppendLine($"Total Files Found,{metrics.TotalFound}");
            report.AppendLine($"Total Files Processed,{processed}");
            report.AppendLine($"Total Files Succeeded,{metrics.Succeeded}");
            report.AppendLine($"Total Files Failed,{metrics.Failed}");
            report.AppendLine($"Total Retries,{metrics.Retries}");
            // The failure count and where to read about those failures belong together: a summary
            // that says 40,000 documents failed and does not say which files hold the detail sends
            // the reader hunting through the report folder.
            report.AppendLine($"Error Report Rows,{ErrorRowCount}");
            report.AppendLine($"Error Report Parts,{ErrorReportPaths.Count}");
            report.AppendLine($"Total Processing Time,{metrics.Elapsed:hh\\:mm\\:ss}");
            // Plain numbers, no thousands separators: these rows are read by spreadsheets and scripts.
            report.AppendLine($"Average Files Per Second,{filesPerSecond.ToString("F2", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Average Files Per Minute,{(filesPerSecond * 60).ToString("F0", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Average Files Per Hour,{(filesPerSecond * 3600).ToString("F0", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Average File Size (MB),{AverageFileSizeMb(totalBytes, metrics.Succeeded).ToString("F2", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Total XHTML Read (GB),{(totalBytes / (1024d * 1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Total RTF Written (GB),{(metrics.BytesWritten / (1024d * 1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture)}");
            report.AppendLine();
            report.AppendLine("--- Per-Folder Breakdown ---");
            report.AppendLine("Date Folder,Total Files,Succeeded,Failed,Processing Time");

            foreach (FolderStatistics folder in metrics.GetFolderStatistics())
            {
                report.AppendLine(string.Join(',',
                    Escape(folder.Name),
                    folder.TotalFiles.ToString(CultureInfo.InvariantCulture),
                    folder.Succeeded.ToString(CultureInfo.InvariantCulture),
                    folder.Failed.ToString(CultureInfo.InvariantCulture),
                    $"{folder.Elapsed:hh\\:mm\\:ss}"));
            }

            AppendWorkerBreakdown(report, metrics);
            AppendPhaseBreakdown(report, metrics);

            File.WriteAllText(SummaryReportPath, report.ToString(), Encoding.UTF8);
            HasSummaryReport = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The migration summary report could not be written under {Path}.", _config.ReportBasePath);
        }
    }

    /// <summary>
    /// Per-worker section of the summary: how many documents each worker slot handled and how
    /// long it took over them.
    /// </summary>
    /// <remarks>
    /// The counts answer "did the work spread evenly?" - with a shared queue they should come out
    /// within a few percent of each other, and one slot far ahead of the rest means the others were
    /// starved. The <c>Avg ms/File</c> column answers the more useful question: whether the workers
    /// were converting in parallel or queueing. It is per-document wall-clock time for one worker,
    /// so it is independent of how many workers ran; if it doubles when the thread count doubles,
    /// the extra threads are waiting on something shared and the run will not get faster.
    /// <para>
    /// Rows count only documents that went through a conversion worker, so files rejected by the
    /// unmatched-file sweep before the batches started are in the totals above but not here.
    /// </para>
    /// </remarks>
    private static void AppendWorkerBreakdown(StringBuilder report, MetricsCollector metrics)
    {
        IReadOnlyList<WorkerStatistics> workers = metrics.GetWorkerStatistics();
        if (workers.Count == 0)
            return;

        double runSeconds = metrics.Elapsed.TotalSeconds;

        report.AppendLine();
        report.AppendLine("--- Per-Worker Breakdown ---");
        report.AppendLine("Worker Slot,Files Processed,Succeeded,Failed,Abandoned,Busy Time,Busy (% of run),Avg ms/File,Files Per Second");

        foreach (WorkerStatistics worker in workers)
        {
            double busySeconds = worker.Busy.TotalSeconds;

            report.AppendLine(string.Join(',',
                worker.Slot.ToString(CultureInfo.InvariantCulture),
                worker.Processed.ToString(CultureInfo.InvariantCulture),
                worker.Succeeded.ToString(CultureInfo.InvariantCulture),
                worker.Failed.ToString(CultureInfo.InvariantCulture),
                worker.Abandoned.ToString(CultureInfo.InvariantCulture),
                $"{worker.Busy:hh\\:mm\\:ss}",
                (runSeconds <= 0 ? 0 : busySeconds / runSeconds * 100d).ToString("F1", CultureInfo.InvariantCulture),
                worker.AverageMillisecondsPerFile.ToString("F1", CultureInfo.InvariantCulture),
                (busySeconds <= 0 ? 0 : worker.Processed / busySeconds).ToString("F2", CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Where the time of a converted document went, summed over the run and split into the two
    /// groups that call for opposite fixes.
    /// </summary>
    /// <remarks>
    /// This is the section to read when adding threads stops adding throughput. The per-worker
    /// service time above says a document got more expensive; this says which part of it did:
    /// <list type="bullet">
    /// <item><description>
    /// <c>Import</c> and <c>Export</c> are Telerik, in-process, CPU-bound. Growing with the thread
    /// count means the machine is out of cores, or the library is serialising internally - more
    /// spindles will not help.
    /// </description></item>
    /// <item><description>
    /// <c>Read</c>, <c>Write</c> and <c>Archive</c> are the file system. Growing with the thread
    /// count means the workers are queueing on the volume, on the NTFS index of a directory they
    /// all write to, or on an on-access virus scanner - more cores will not help.
    /// </description></item>
    /// </list>
    /// The percentages are of measured time, not of the run: a worker also spends time on
    /// bookkeeping and on retry backoff, and the <c>Unattributed</c> row is what is left of its
    /// busy time once every phase is accounted for. A large one is itself a finding - it is time
    /// spent inside the pipeline rather than inside a conversion.
    /// </remarks>
    private static void AppendPhaseBreakdown(StringBuilder report, MetricsCollector metrics)
    {
        ConversionPhases phases = metrics.PhaseTotals;
        if (phases.TotalTicks == 0)
            return;

        long converted = metrics.Succeeded;
        double measuredMs = TicksToMilliseconds(phases.TotalTicks);

        report.AppendLine();
        report.AppendLine("--- Phase Breakdown (successful conversions) ---");
        report.AppendLine("Phase,Kind,Total Seconds,% of Measured,Avg ms/File");

        AppendPhase(report, "Read input", "Disk", phases.ReadTicks, converted, measuredMs);
        AppendPhase(report, "Decode Base64/charset", "CPU", phases.DecodeTicks, converted, measuredMs);
        AppendPhase(report, "Validate content", "CPU", phases.ValidateTicks, converted, measuredMs);
        AppendPhase(report, "Telerik import (XHTML)", "CPU", phases.ImportTicks, converted, measuredMs);
        AppendPhase(report, "Telerik export (RTF)", "CPU", phases.ExportTicks, converted, measuredMs);
        AppendPhase(report, "Write output", "Disk", phases.WriteTicks, converted, measuredMs);
        AppendPhase(report, "Archive input", "Disk", phases.ArchiveTicks, converted, measuredMs);

        long cpuTicks = phases.DecodeTicks + phases.ValidateTicks + phases.ImportTicks + phases.ExportTicks;
        long diskTicks = phases.ReadTicks + phases.WriteTicks + phases.ArchiveTicks;

        report.AppendLine();
        AppendPhase(report, "CPU total", "CPU", cpuTicks, converted, measuredMs);
        AppendPhase(report, "Disk total", "Disk", diskTicks, converted, measuredMs);

        // Worker busy time that no phase claimed: slot bookkeeping, trace and report writing,
        // retry backoff, and the failed documents that never reached a phase at all.
        double busyMs = metrics.AverageServiceTimeMs * Math.Max(1, metrics.Processed);
        double unattributedMs = busyMs - measuredMs;
        if (unattributedMs > 0)
        {
            report.AppendLine(string.Join(',',
                "Unattributed",
                "Overhead",
                (unattributedMs / 1000d).ToString("F1", CultureInfo.InvariantCulture),
                (unattributedMs / measuredMs * 100d).ToString("F1", CultureInfo.InvariantCulture),
                (converted == 0 ? 0 : unattributedMs / converted).ToString("F3", CultureInfo.InvariantCulture)));
        }
    }

    private static void AppendPhase(
        StringBuilder report, string name, string kind, long ticks, long files, double measuredMs)
    {
        double ms = TicksToMilliseconds(ticks);

        report.AppendLine(string.Join(',',
            Escape(name),
            kind,
            (ms / 1000d).ToString("F1", CultureInfo.InvariantCulture),
            (measuredMs <= 0 ? 0 : ms / measuredMs * 100d).ToString("F1", CultureInfo.InvariantCulture),
            (files == 0 ? 0 : ms / files).ToString("F3", CultureInfo.InvariantCulture)));
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static double AverageFileSizeMb(long totalBytes, long fileCount) =>
        fileCount == 0 ? 0 : totalBytes / (1024d * 1024d) / fileCount;

    /// <summary>
    /// Average concurrency as a percentage of the workers that were asked for. 100% means every
    /// configured worker was occupied for the whole run; a low figure means the parallelism
    /// setting is not buying what it claims to.
    /// </summary>
    private double WorkerUtilisation(MetricsCollector metrics)
    {
        int configured = _config.Processing.EffectiveParallelism;
        return configured <= 0 ? 0 : Math.Min(100d, metrics.AverageConcurrency / configured * 100d);
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    public void Dispose()
    {
        lock (_errorLock)
        {
            try
            {
                _errorReport.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The error report could not be closed cleanly.");
            }
        }
    }
}
