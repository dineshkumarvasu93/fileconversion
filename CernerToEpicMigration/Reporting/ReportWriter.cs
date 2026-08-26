using System.Diagnostics;
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
public sealed class ReportWriter : IDisposable
{
    /// <summary>Rows buffered before a flush is forced, however recent the last one was.</summary>
    private const int FlushRowThreshold = 500;

    /// <summary>How stale the on-disk error report is allowed to get while a run is going.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly MigrationConfig _config;
    private readonly ILogger<ReportWriter> _logger;
    private readonly string _timestamp;
    private readonly object _errorLock = new();
    private readonly Stopwatch _sinceLastFlush = new();

    private StreamWriter? _errorWriter;
    private int _rowsSinceLastFlush;

    public ReportWriter(MigrationConfig config, ILogger<ReportWriter> logger)
    {
        _config = config;
        _logger = logger;
        _timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Run stamp shared by every file this run produces, so the summary, the error report and the
    /// file trace can be matched up by name.
    /// </summary>
    public string Timestamp => _timestamp;

    public string SummaryReportPath =>
        Path.Combine(Path.GetFullPath(_config.ReportBasePath), $"migration_report_{_timestamp}.csv");

    public string ErrorReportPath =>
        Path.Combine(Path.GetFullPath(_config.ReportBasePath), $"error_report_{_timestamp}.csv");

    /// <summary>True once at least one failure row has been written.</summary>
    public bool HasErrorReport { get; private set; }

    /// <summary>True once the end-of-run summary has been written to disk.</summary>
    public bool HasSummaryReport { get; private set; }

    /// <summary>Appends one row to the error report. Safe to call from worker threads.</summary>
    public void RecordFailure(FileFailure failure)
    {
        lock (_errorLock)
        {
            try
            {
                if (_errorWriter is null)
                {
                    Directory.CreateDirectory(Path.GetFullPath(_config.ReportBasePath));

                    // FileShare.ReadWrite so an operator can open or tail the error report while a
                    // multi-hour run is still going.
                    FileStream stream = new(ErrorReportPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    _errorWriter = new StreamWriter(stream, Encoding.UTF8);
                    _errorWriter.WriteLine("File Path,Error Category,Error Message");
                    _sinceLastFlush.Start();
                }

                _errorWriter.WriteLine(string.Join(',',
                    Escape(failure.FilePath),
                    Escape(failure.Category.ToString()),
                    Escape(failure.ErrorMessage)));
                HasErrorReport = true;

                // Flushing every row makes the error path a disk round-trip per failure, and every
                // worker queues behind this lock to take it. Batching keeps a tailed report at most
                // FlushInterval behind while a run where a systemic fault fails millions of
                // documents no longer stalls on the reporting.
                if (++_rowsSinceLastFlush >= FlushRowThreshold || _sinceLastFlush.Elapsed >= FlushInterval)
                    FlushCore();
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
            FlushCore();
        }
    }

    /// <summary>Flush without taking the lock; callers already hold it.</summary>
    private void FlushCore()
    {
        _errorWriter?.Flush();
        _rowsSinceLastFlush = 0;
        _sinceLastFlush.Restart();
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
            _errorWriter?.Dispose();
            _errorWriter = null;
        }
    }
}
