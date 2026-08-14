using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;

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
    private readonly object _collisionLock = new();

    public FileManager(MigrationConfig config)
    {
        _config = config;
    }

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
            // Collisions are the exception, so the lock only guards this slow path.
            lock (_collisionLock)
            {
                string uniquePath = BuildUniquePath(destinationFolder, fileName);
                File.Move(sourcePath, uniquePath);
                return uniquePath;
            }
        }
    }

    private static string BuildUniquePath(string folder, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int suffix = 1; suffix < int.MaxValue; suffix++)
        {
            string candidate = Path.Combine(folder, $"{stem}_{suffix}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Unable to find a free file name for {fileName} in {folder}.");
    }
}
