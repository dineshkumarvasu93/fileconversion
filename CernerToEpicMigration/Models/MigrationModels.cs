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

/// <summary>
/// One input document queued for conversion.
/// </summary>
/// <param name="BatchId">
/// The batch this document belonged to, e.g. <c>2026-08-01#0007</c>. It is carried on the work
/// item rather than looked up later because it is the only handle that ties a row of the error
/// report back to a point in the run log: the pipeline logs one line per completed batch, so a
/// batch id turns "this file failed" into "this file failed in the batch that ran at 03:14".
/// Files rejected before batching carry <see cref="ScanBatchId"/>.
/// </param>
public sealed record WorkItem(string FilePath, DateFolder Folder, string BatchId = WorkItem.ScanBatchId)
{
    /// <summary>Batch id for documents rejected during discovery, before any batch was formed.</summary>
    public const string ScanBatchId = "scan";

    /// <summary>
    /// Path of the document relative to its date folder: <c>doc_a.xhtml</c> on a flat drop,
    /// <c>patient_001\doc_a.xhtml</c> when the extract nests by patient.
    /// </summary>
    /// <remarks>
    /// This, not the bare file name, is what the output, archive and error locations are built
    /// from, so the patient folder is carried through every stage instead of being flattened
    /// away. Flattening would be actively unsafe here: two patients with a <c>doc_1.xhtml</c>
    /// each would collide, and the collision handler would rename one to <c>doc_1_1.xhtml</c> -
    /// quietly severing the only link between a document and the patient it belongs to.
    /// </remarks>
    public string RelativePath => Path.GetRelativePath(Folder.Path, FilePath);

    /// <summary>
    /// The patient sub-folder the document sits in, or empty on a flat date folder. Used to
    /// mirror that structure under the output, archive and error folders.
    /// </summary>
    public string SubFolder => Path.GetDirectoryName(RelativePath) ?? string.Empty;

    /// <summary>Formats the batch id of the n-th batch of a date folder.</summary>
    public static string FormatBatchId(string dateFolder, int batchNumber) =>
        $"{dateFolder}#{batchNumber:D4}";
}

/// <summary>Why a document was failed, which decides how an operator should act on it.</summary>
public enum FailureSource
{
    /// <summary>The document was converted and the conversion failed.</summary>
    Conversion,

    /// <summary>The document was rejected before conversion - it was never a candidate.</summary>
    Discovery
}

/// <summary>
/// Where a failed document and its log ended up. Returned by
/// <c>FileManager.MoveToError</c> so the error report can name both.
/// </summary>
/// <param name="FileName">Name in the error folder - suffixed if the name was already taken.</param>
/// <param name="ErrorFilePath">Full path in the error folder, or null when the move failed.</param>
/// <param name="ErrorLogFileName">Name of the per-file log, or null when none was written.</param>
/// <param name="MoveError">Why the document could not be moved, or null when it was.</param>
public readonly record struct ErrorPlacement(
    string FileName,
    string? ErrorFilePath,
    string? ErrorLogFileName,
    string? MoveError)
{
    public bool Moved => ErrorFilePath is not null && MoveError is null;
}

/// <summary>
/// A file that exhausted its attempts and was moved to the error folder: one row of the error
/// report and the content of one <c>.error.log</c>.
/// </summary>
/// <remarks>
/// Everything after <paramref name="StackTrace"/> is filled in as the failure is handled rather
/// than at the point it is thrown - the error folder placement is not known until the move has
/// been attempted - so a failure is built once and then completed with <c>with</c>.
/// </remarks>
/// <param name="FilePath">Full path the document had in the input folder.</param>
/// <param name="BatchId">Batch the document was in; <see cref="WorkItem.ScanBatchId"/> if rejected before batching.</param>
/// <param name="SubFolder">Patient sub-folder within the date folder, or empty on a flat drop.</param>
/// <param name="Source">Whether the conversion failed or the document was rejected up front.</param>
/// <param name="Reason">Plain-language reason, for someone triaging a report of thousands of rows.</param>
/// <param name="FileSizeBytes">Size on disk, or 0 when it could not be read.</param>
/// <param name="ErrorLogFileName">Name of the <c>.error.log</c> written beside it, if any.</param>
/// <param name="ErrorFilePath">Where the document now is, or null when it could not be moved.</param>
/// <param name="MoveError">Why the move failed, or null when it succeeded.</param>
public sealed record FileFailure(
    string FilePath,
    string DateFolder,
    ErrorCategory Category,
    string ErrorType,
    string ErrorMessage,
    int Attempts,
    DateTimeOffset TimestampUtc,
    string? StackTrace,
    string BatchId = WorkItem.ScanBatchId,
    string SubFolder = "",
    FailureSource Source = FailureSource.Conversion,
    string? Reason = null,
    long FileSizeBytes = 0,
    string? ErrorLogFileName = null,
    string? ErrorFilePath = null,
    string? MoveError = null)
{
    /// <summary>
    /// Name the document goes by now: the one in the error folder, which is the name suffixed
    /// with <c>_1</c>, <c>_2</c> and so on when a re-run collided with an earlier copy.
    /// </summary>
    public string FileName => Path.GetFileName(ErrorFilePath ?? FilePath);

    /// <summary>
    /// The name the document arrived with. Identical to <see cref="FileName"/> unless the move
    /// had to suffix it - which is exactly when an operator needs both to find the file again.
    /// </summary>
    public string ActualFileName => Path.GetFileName(FilePath);

    /// <summary>True when the document is in the error folder rather than still in the input folder.</summary>
    public bool MovedToError => ErrorFilePath is not null && MoveError is null;

    /// <summary>The reason if one was given, otherwise one derived from the exception.</summary>
    public string ReasonText => Reason ?? $"{Category} failure ({ErrorType})";
}

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
    string BatchId,
    string FileName,
    string SubFolder,
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
