using System.Diagnostics;
using System.Globalization;
using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;

namespace CernerToEpicMigration.Reporting;

/// <summary>
/// Optional per-file trace: one row per document recording which worker slot handled it and
/// when. Off unless <c>Processing.EnableFileTrace</c> is set.
/// </summary>
/// <remarks>
/// This exists to answer "are my configured workers actually working?". The summary report gives
/// the peak and average concurrency as two numbers; the trace gives the intervals those numbers
/// were computed from, so the claim can be checked independently - count the overlapping
/// <c>[start, end)</c> ranges and the answer must match the reported peak.
/// <para>
/// The worker slot, not the thread id, is the identity to group by. A slot is held for the whole
/// of one file; the thread can change under it at the <c>await</c> in the retry path, so the same
/// document can start on thread 12 and finish on thread 31. Both columns are written - the thread
/// id is useful when correlating with an external profiler, and misleading for anything else.
/// </para>
/// </remarks>
public sealed class FileTraceWriter : IDisposable
{
    /// <summary>Rows buffered before a flush is forced, however recent the last one was.</summary>
    private const int FlushRowThreshold = 500;

    /// <summary>How stale the on-disk trace is allowed to get while a run is going.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly MigrationConfig _config;
    private readonly object _writeLock = new();
    private readonly Stopwatch _sinceLastFlush = new();

    private StreamWriter? _writer;
    private int _rowsSinceLastFlush;

    public FileTraceWriter(MigrationConfig config, string timestamp)
    {
        _config = config;
        TracePath = Path.Combine(
            Path.GetFullPath(config.ReportBasePath), $"file_trace_{timestamp}.csv");
    }

    public string TracePath { get; }

    public bool IsEnabled => _config.Processing.EnableFileTrace;

    /// <summary>True once at least one row has been written.</summary>
    public bool HasTrace { get; private set; }

    /// <summary>Appends one row. Safe to call from worker threads; a no-op when the trace is off.</summary>
    public void Record(FileTrace trace)
    {
        if (!IsEnabled)
            return;

        lock (_writeLock)
        {
            if (_writer is null)
            {
                Directory.CreateDirectory(Path.GetFullPath(_config.ReportBasePath));

                // FileShare.ReadWrite so the trace can be tailed or opened while the run is going.
                FileStream stream = new(TracePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, Encoding.UTF8);
                _writer.WriteLine(
                    "Worker Slot,Thread Id,Date Folder,File Name,Start Utc,End Utc,Duration Ms,Attempts,Outcome");
                _sinceLastFlush.Start();
            }

            _writer.WriteLine(string.Join(',',
                trace.WorkerSlot.ToString(CultureInfo.InvariantCulture),
                trace.ThreadId.ToString(CultureInfo.InvariantCulture),
                Escape(trace.DateFolder),
                Escape(trace.FileName),
                trace.StartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                (trace.StartUtc + trace.Duration).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                trace.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                trace.Attempts.ToString(CultureInfo.InvariantCulture),
                Escape(trace.Outcome)));

            HasTrace = true;

            // Same reasoning as the error report: a flush per row would put a disk round-trip in
            // the hot path of every single document, which is the one place this must not cost
            // anything measurable - the trace is there to measure the run, not to change it.
            if (++_rowsSinceLastFlush >= FlushRowThreshold || _sinceLastFlush.Elapsed >= FlushInterval)
                FlushCore();
        }
    }

    /// <summary>Writes any buffered rows to disk. Called at each batch boundary.</summary>
    public void Flush()
    {
        lock (_writeLock)
        {
            FlushCore();
        }
    }

    private void FlushCore()
    {
        _writer?.Flush();
        _rowsSinceLastFlush = 0;
        _sinceLastFlush.Restart();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string sanitised = value.Replace("\r", " ").Replace("\n", " ");
        return sanitised.Contains(',') || sanitised.Contains('"')
            ? $"\"{sanitised.Replace("\"", "\"\"")}\""
            : sanitised;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
