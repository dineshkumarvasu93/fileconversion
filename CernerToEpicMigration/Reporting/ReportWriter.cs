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
    private readonly MigrationConfig _config;
    private readonly ILogger<ReportWriter> _logger;
    private readonly string _timestamp;
    private readonly object _errorLock = new();

    private StreamWriter? _errorWriter;

    public ReportWriter(MigrationConfig config, ILogger<ReportWriter> logger)
    {
        _config = config;
        _logger = logger;
        _timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    }

    public string SummaryReportPath =>
        Path.Combine(Path.GetFullPath(_config.ReportBasePath), $"migration_report_{_timestamp}.csv");

    public string ErrorReportPath =>
        Path.Combine(Path.GetFullPath(_config.ReportBasePath), $"error_report_{_timestamp}.csv");

    /// <summary>True once at least one failure row has been written.</summary>
    public bool HasErrorReport { get; private set; }

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
                    // multi-hour run is still going; AutoFlush so what they see is current.
                    FileStream stream = new(ErrorReportPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    _errorWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    _errorWriter.WriteLine("File Path,Error Category,Error Message");
                }

                _errorWriter.WriteLine(string.Join(',',
                    Escape(failure.FilePath),
                    Escape(failure.Category.ToString()),
                    Escape(failure.ErrorMessage)));
                HasErrorReport = true;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The failure of {File} could not be added to the error report.", failure.FilePath);
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
                _errorWriter?.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The error report under {Path} could not be closed cleanly.", _config.ReportBasePath);
            }
            finally
            {
                _errorWriter = null;
            }
        }
    }
}
