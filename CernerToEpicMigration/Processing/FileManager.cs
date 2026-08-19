using System.Collections.Concurrent;
using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using Microsoft.Extensions.Logging;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Owns the file lifecycle of design document section 7.4: where the RTF is written,
/// and where the input document goes once it has succeeded or exhausted its retries.
/// </summary>
public sealed class FileManager
{
    public const string ArchiveFolderName = "archive";
    public const string ErrorFolderName = "error";

    private readonly MigrationConfig _config;
    private readonly ILogger<FileManager> _logger;

    /// <summary>
    /// Next <c>_N</c> suffix to try for a destination path this run has already collided on,
    /// so a re-run over a populated archive does not restart the probe at <c>_1</c> every time.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _nextSuffix = new(StringComparer.OrdinalIgnoreCase);

    private int _errorLogsWritten;
    private int _errorLogsSuppressed;

    public FileManager(MigrationConfig config, ILogger<FileManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Per-file error logs not written because <c>MaxErrorLogFiles</c> was reached.</summary>
    public int ErrorLogsSuppressed => Volatile.Read(ref _errorLogsSuppressed);

    /// <summary>RTF destination for an input document, mirroring the date folder structure.</summary>
    public string GetRtfOutputPath(WorkItem item)
    {
        string fileName = Path.ChangeExtension(Path.GetFileName(item.FilePath), ".rtf");
        return Path.Combine(Path.GetFullPath(_config.OutputRtfBasePath), item.Folder.Name, fileName);
    }

    /// <summary>Moves a successfully converted input document to <c>{dateFolder}\archive</c>.</summary>
    public void ArchiveSuccess(WorkItem item)
    {
        if (!_config.Processing.ArchiveOnSuccess)
            return;

        string archiveFolder = Path.Combine(item.Folder.Path, ArchiveFolderName);
        MoveWithoutOverwriting(item.FilePath, archiveFolder);
    }

    /// <summary>
    /// Moves a failed input document to <c>{dateFolder}\error</c> and writes the
    /// per-file error log described in section 10.3.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the document was moved, otherwise why it could not be moved. A
    /// document that is still locked by another process stays in the input folder, but
    /// its error log is written either way so the failure is never invisible.
    /// </returns>
    public string? MoveToError(WorkItem item, FileFailure failure)
    {
        string errorFolder = Path.Combine(item.Folder.Path, ErrorFolderName);
        Directory.CreateDirectory(errorFolder);

        string fileName = Path.GetFileName(item.FilePath);
        string? moveError = null;

        try
        {
            fileName = Path.GetFileName(MoveWithoutOverwriting(item.FilePath, errorFolder));
        }
        catch (IOException exception)
        {
            moveError = exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            moveError = exception.Message;
        }

        if (!ShouldWriteErrorLog())
            return moveError;

        string logPath = Path.Combine(errorFolder, Path.GetFileNameWithoutExtension(fileName) + ".error.log");

        StringBuilder log = new();
        log.AppendLine($"File: {fileName}");
        log.AppendLine($"Original File: {Path.GetFileName(item.FilePath)}");
        log.AppendLine($"Date Folder: {item.Folder.Name}");
        log.AppendLine($"Error Time: {failure.TimestampUtc:yyyy-MM-ddTHH:mm:ssZ}");
        log.AppendLine($"Category: {failure.Category}");
        log.AppendLine($"Attempts: {failure.Attempts}");
        log.AppendLine($"Last Error: {failure.ErrorType}: {failure.ErrorMessage}");

        if (moveError is not null)
            log.AppendLine($"File Location: left in the input folder - move failed: {moveError}");

        log.AppendLine($"Stack Trace: {failure.StackTrace ?? "(none)"}");

        File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
        return moveError;
    }

    /// <summary>
    /// Moves a file into <paramref name="destinationFolder"/>. If a file of that name is
    /// already there (a re-run of the same date folder), a numeric suffix is added rather
    /// than silently discarding either copy.
    /// </summary>
    private string MoveWithoutOverwriting(string sourcePath, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);

        string fileName = Path.GetFileName(sourcePath);
        string destinationPath = Path.Combine(destinationFolder, fileName);

        try
        {
            File.Move(sourcePath, destinationPath);
            return destinationPath;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            return MoveToSuffixedPath(sourcePath, destinationFolder, fileName, destinationPath);
        }
    }

    /// <summary>
    /// Slow path for a destination name that is already taken. Re-running a date folder whose
    /// archive was never cleared collides on every single file, so this path has to stay
    /// parallel: the move itself is the collision check (<see cref="File.Move(string, string)"/>
    /// fails rather than overwriting), which means no lock is needed, and the probe starts from
    /// the highest suffix this run has already used for the same name instead of from 1.
    /// </summary>
    private string MoveToSuffixedPath(string sourcePath, string destinationFolder, string fileName, string takenPath)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int suffix = _nextSuffix.GetValueOrDefault(takenPath, 1); suffix < int.MaxValue; suffix++)
        {
            string candidate = Path.Combine(destinationFolder, $"{stem}_{suffix}{extension}");

            try
            {
                File.Move(sourcePath, candidate);
                _nextSuffix[takenPath] = suffix + 1;
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken by an earlier run or by another worker; try the next suffix.
            }
        }

        throw new IOException($"Unable to find a free file name for {fileName} in {destinationFolder}.");
    }

    /// <summary>
    /// Whether this failure still gets a per-file <c>.error.log</c>. Every failure is always in
    /// the error report CSV and in the run log; the per-file log is the convenience copy, and on
    /// a run where a systemic problem fails millions of documents writing one file per failure
    /// is what turns a slow run into a stalled one.
    /// </summary>
    private bool ShouldWriteErrorLog()
    {
        int limit = _config.Processing.MaxErrorLogFiles;
        if (limit <= 0)
            return true;

        if (Interlocked.Increment(ref _errorLogsWritten) <= limit)
            return true;

        if (Interlocked.Increment(ref _errorLogsSuppressed) == 1)
        {
            _logger.LogWarning(
                "MaxErrorLogFiles ({Limit:N0}) reached; further failures are recorded in the error report and " +
                "the run log only, and the documents are still moved to the error folder.", limit);
        }

        return false;
    }
}
