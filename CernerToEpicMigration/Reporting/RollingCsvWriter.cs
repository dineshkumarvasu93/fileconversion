using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CernerToEpicMigration.Reporting;

/// <summary>
/// Append-only CSV writer that splits its output into numbered part files.
/// </summary>
/// <remarks>
/// A single-file report is fine for a tuning run and useless for a production one. A million
/// documents produce a million trace rows, and a systemic fault produces a million error rows:
/// one CSV of that size will not open in Excel, is slow to grep, and cannot be handed to two
/// people to review in parallel. So every row count is capped and the writer rolls to
/// <c>{baseName}_002.csv</c>, <c>_003</c> and so on, which keeps each part openable and lets an
/// operator work through the failures part by part.
/// <para>
/// Parts are numbered from <c>_001</c> even when there is only one, so the name never changes
/// meaning halfway through a run and a directory listing sorts in write order.
/// </para>
/// <para>
/// Every part is opened with <see cref="FileShare.ReadWrite"/> and flushed on a row threshold or
/// a time interval, so a part can be opened or tailed while the run that is writing it is still
/// going. Flushing per row would put a disk round-trip in the hot path of every document, which
/// is the one place reporting must not cost anything measurable.
/// </para>
/// </remarks>
public sealed class RollingCsvWriter : IDisposable
{
    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _header;
    private readonly int _maxRowsPerPart;
    private readonly int _flushRowThreshold;
    private readonly TimeSpan _flushInterval;
    private readonly object _writeLock = new();
    private readonly Stopwatch _sinceLastFlush = new();
    private readonly List<string> _parts = new();

    private StreamWriter? _writer;
    private int _partNumber;
    private int _rowsInPart;
    private int _rowsSinceLastFlush;
    private long _totalRows;

    /// <param name="maxRowsPerPart">Rows per part file; 0 or less writes one unbounded file.</param>
    public RollingCsvWriter(
        string directory,
        string baseName,
        string header,
        int maxRowsPerPart,
        int flushRowThreshold,
        TimeSpan flushInterval)
    {
        _directory = Path.GetFullPath(directory);
        _baseName = baseName;
        _header = header;
        _maxRowsPerPart = maxRowsPerPart;
        _flushRowThreshold = flushRowThreshold;
        _flushInterval = flushInterval;

        FirstPartPath = PartPath(1);
    }

    /// <summary>
    /// Path of part 1. Known before anything is written, so it can be printed and probed even on
    /// a run that never produces a row.
    /// </summary>
    public string FirstPartPath { get; }

    /// <summary>Every part written so far, in order. Empty until the first row.</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            lock (_writeLock)
            {
                return _parts.ToArray();
            }
        }
    }

    /// <summary>Rows written across all parts, excluding the headers.</summary>
    public long RowCount => Interlocked.Read(ref _totalRows);

    /// <summary>True once at least one row has been written.</summary>
    public bool HasRows => RowCount > 0;

    /// <summary>
    /// Appends one row, escaping each field. Safe to call from worker threads; the first call
    /// creates the report folder and part 1.
    /// </summary>
    public void Write(params string?[] fields)
    {
        lock (_writeLock)
        {
            if (_writer is null || (_maxRowsPerPart > 0 && _rowsInPart >= _maxRowsPerPart))
                StartNextPart();

            _writer!.WriteLine(string.Join(',', fields.Select(Escape)));

            _rowsInPart++;
            Interlocked.Increment(ref _totalRows);

            if (++_rowsSinceLastFlush >= _flushRowThreshold || _sinceLastFlush.Elapsed >= _flushInterval)
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

    /// <summary>Closes the current part and opens the next one, header first.</summary>
    private void StartNextPart()
    {
        _writer?.Flush();
        _writer?.Dispose();

        Directory.CreateDirectory(_directory);

        string path = PartPath(++_partNumber);
        FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, Encoding.UTF8);
        _writer.WriteLine(_header);

        _parts.Add(path);
        _rowsInPart = 0;
        _sinceLastFlush.Restart();
    }

    private string PartPath(int partNumber) =>
        Path.Combine(_directory, $"{_baseName}_{partNumber.ToString("D3", CultureInfo.InvariantCulture)}.csv");

    /// <summary>Flush without taking the lock; callers already hold it.</summary>
    private void FlushCore()
    {
        _writer?.Flush();
        _rowsSinceLastFlush = 0;
        _sinceLastFlush.Restart();
    }

    /// <summary>
    /// CSV-escapes one field. Carriage returns and line feeds are kept - a quoted field may
    /// legally span lines - so an error message reads the way it was thrown.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
