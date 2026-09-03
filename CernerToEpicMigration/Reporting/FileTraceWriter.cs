using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using System.Diagnostics;
using System.Globalization;

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
/// <para>
/// At roughly 130 bytes a row a million-document run traces about 130 MB, which is why the trace
/// is written in parts of <c>Processing.MaxReportRowsPerFile</c> rows rather than as one file:
/// a part opens in a spreadsheet, a 130 MB CSV does not. The batch id column is what joins a
/// trace row back to the error report and to the per-batch lines in the run log.
/// </para>
/// </remarks>
public sealed class FileTraceWriter : IDisposable
{
    /// <summary>Rows buffered before a flush is forced, however recent the last one was.</summary>
    private const int FlushRowThreshold = 500;

    /// <summary>How stale the on-disk trace is allowed to get while a run is going.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Milliseconds a <see cref="Stopwatch"/> tick is worth. The phase columns are recorded in
    /// ticks so they can be summed losslessly; they are written in milliseconds because that is
    /// what a person reading a row needs.
    /// </summary>
    private static readonly double MillisecondsPerTick = 1000d / Stopwatch.Frequency;

    private const string TraceHeader =
        "Worker Slot,Thread Id,Date Folder,Batch Id,Sub Folder,File Name,Start Utc,End Utc,Duration Ms,Attempts,Outcome," +
        "Read Ms,Decode Ms,Validate Ms,Import Ms,Export Ms,Write Ms,Archive Ms";

    private readonly MigrationConfig _config;
    private readonly RollingCsvWriter _trace;

    public FileTraceWriter(MigrationConfig config, string timestamp)
    {
        _config = config;
        _trace = new RollingCsvWriter(
            config.ReportBasePath,
            $"file_trace_{timestamp}",
            TraceHeader,
            config.Processing.MaxReportRowsPerFile,
            FlushRowThreshold,
            FlushInterval);
    }

    /// <summary>First part of the trace. Known before any row is written.</summary>
    public string TracePath => _trace.FirstPartPath;

    /// <summary>Every trace part written so far, in order.</summary>
    public IReadOnlyList<string> TracePaths => _trace.Parts;

    public bool IsEnabled => _config.Processing.EnableFileTrace;

    /// <summary>True once at least one row has been written.</summary>
    public bool HasTrace => _trace.HasRows;

    /// <summary>Rows written across every part.</summary>
    public long RowCount => _trace.RowCount;

    /// <summary>Appends one row. Safe to call from worker threads; a no-op when the trace is off.</summary>
    public void Record(FileTrace trace)
    {
        if (!IsEnabled)
            return;

        _trace.Write(
            trace.WorkerSlot.ToString(CultureInfo.InvariantCulture),
            trace.ThreadId.ToString(CultureInfo.InvariantCulture),
            trace.DateFolder,
            trace.BatchId,
            trace.SubFolder,
            trace.FileName,
            trace.StartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            (trace.StartUtc + trace.Duration).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            trace.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
            trace.Attempts.ToString(CultureInfo.InvariantCulture),
            trace.Outcome,
            Milliseconds(trace.Phases.ReadTicks),
            Milliseconds(trace.Phases.DecodeTicks),
            Milliseconds(trace.Phases.ValidateTicks),
            Milliseconds(trace.Phases.ImportTicks),
            Milliseconds(trace.Phases.ExportTicks),
            Milliseconds(trace.Phases.WriteTicks),
            Milliseconds(trace.Phases.ArchiveTicks));
    }

    /// <summary>
    /// One phase duration as milliseconds. Three decimals because a decode or a directory probe
    /// is routinely under a tenth of a millisecond, and rounding those to zero would hide exactly
    /// the per-file overhead the trace is there to expose.
    /// </summary>
    private static string Milliseconds(long ticks) =>
        (ticks * MillisecondsPerTick).ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>Writes any buffered rows to disk. Called at each batch boundary.</summary>
    public void Flush() => _trace.Flush();

    public void Dispose() => _trace.Dispose();
}
