using System.Globalization;
using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Monitoring;
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

            File.WriteAllText(SummaryReportPath, report.ToString(), Encoding.UTF8);
            HasSummaryReport = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The migration summary report could not be written under {Path}.", _config.ReportBasePath);
        }
    }

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
