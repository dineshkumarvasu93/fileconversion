namespace CernerToEpicMigration.Models;

/// <summary>Error categories from design document section 10.1.</summary>
public enum ErrorCategory
{
    /// <summary>Retryable - file locked, I/O timeout, memory pressure.</summary>
    Transient,

    /// <summary>Not retryable - corrupt XHTML, unsupported format, parsing exception.</summary>
    Permanent,

    /// <summary>Stops the run - disk full, permission denied, license expired.</summary>
    Fatal
}

/// <summary>A date-wise input folder, e.g. <c>D:\Migration\Input\2026-08-01</c>.</summary>
public sealed record DateFolder(string Name, string Path);

/// <summary>How many input documents a date folder holds and how large they are.</summary>
public readonly record struct FolderScan(int Files, long Bytes);

/// <summary>One input document queued for conversion.</summary>
public sealed record WorkItem(string FilePath, DateFolder Folder);

/// <summary>A file that exhausted its attempts and was moved to the error folder.</summary>
public sealed record FileFailure(
    string FileName,
    string DateFolder,
    ErrorCategory Category,
    string ErrorType,
    string ErrorMessage,
    int Attempts,
    DateTimeOffset TimestampUtc,
    string? StackTrace);

/// <summary>
/// One row of the per-file trace: which worker handled which document, and for how long.
/// <paramref name="Duration"/> is measured with a stopwatch rather than by subtracting two wall
/// clock readings, so it stays honest across a clock adjustment mid-run; the end of the interval
/// is <paramref name="StartUtc"/> plus <paramref name="Duration"/>.
/// </summary>
public readonly record struct FileTrace(
    int WorkerSlot,
    int ThreadId,
    string DateFolder,
    string FileName,
    DateTimeOffset StartUtc,
    TimeSpan Duration,
    int Attempts,
    string Outcome);

/// <summary>Per-date-folder totals used by the dashboard and the summary report.</summary>
public sealed class FolderStatistics
{
    public required string Name { get; init; }

    public int TotalFiles;

    public int Succeeded;

    public int Failed;

    public TimeSpan Elapsed { get; set; }

    public int Processed => Succeeded + Failed;
}

/// <summary>Outcome of a Stage 1 run.</summary>
public sealed class Stage1Result
{
    public bool Completed { get; init; }

    public bool Cancelled { get; init; }

    public Exception? FatalError { get; init; }

    public required IReadOnlyList<FolderStatistics> Folders { get; init; }
}
