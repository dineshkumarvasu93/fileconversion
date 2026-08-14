using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using Microsoft.Extensions.Logging;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Finds the date-wise input folders and the documents inside them
/// (design document sections 6 and 7.1).
/// </summary>
public sealed class FileDiscoveryService
{
    /// <summary>Sub-folders of a date folder that hold processed files, never input.</summary>
    private static readonly string[] ReservedFolderNames = { FileManager.ArchiveFolderName, FileManager.ErrorFolderName };

    private readonly MigrationConfig _config;
    private readonly ILogger<FileDiscoveryService> _logger;

    public FileDiscoveryService(MigrationConfig config, ILogger<FileDiscoveryService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Returns the date folders to process, ordered by name. When
    /// <paramref name="onlyDateFolder"/> is set, only that folder is returned.
    /// </summary>
    public IReadOnlyList<DateFolder> DiscoverFolders(string? onlyDateFolder)
    {
        string basePath = Path.GetFullPath(_config.InputBasePath);

        if (!string.IsNullOrWhiteSpace(onlyDateFolder))
        {
            string path = Path.Combine(basePath, onlyDateFolder);
            if (!Directory.Exists(path))
            {
                _logger.LogError("Date folder not found: {Path}", path);
                return Array.Empty<DateFolder>();
            }

            return new[] { new DateFolder(onlyDateFolder, path) };
        }

        List<DateFolder> folders = Directory.EnumerateDirectories(basePath)
            .Select(path => new DateFolder(Path.GetFileName(path), path))
            .Where(folder => !ReservedFolderNames.Contains(folder.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(folder => folder.Name, StringComparer.Ordinal)
            .ToList();

        if (folders.Count > 0)
            return folders;

        // Fall back to treating the base path itself as one folder so a flat
        // drop of documents can still be processed.
        if (CountFiles(new DateFolder(Path.GetFileName(basePath), basePath)) > 0)
        {
            _logger.LogWarning(
                "No date-wise sub-folders found under {BasePath}; processing the base folder itself. " +
                "The expected layout is {BasePath}\\yyyy-MM-dd\\*.xhtml.", basePath, basePath);

            return new[] { new DateFolder(Path.GetFileName(basePath.TrimEnd(Path.DirectorySeparatorChar)), basePath) };
        }

        return Array.Empty<DateFolder>();
    }

    /// <summary>Streams the input documents of one date folder (top level only).</summary>
    public IEnumerable<string> EnumerateFiles(DateFolder folder) =>
        Directory.EnumerateFiles(folder.Path, _config.Processing.FileSearchPattern, SearchOption.TopDirectoryOnly);

    /// <summary>
    /// Counts the input documents of one date folder and totals their size, without
    /// materialising the list. File sizes come from the directory entry itself, so this
    /// costs no more than counting.
    /// </summary>
    public FolderScan Scan(DateFolder folder)
    {
        int files = 0;
        long bytes = 0;

        foreach (FileInfo file in new DirectoryInfo(folder.Path)
                     .EnumerateFiles(_config.Processing.FileSearchPattern, SearchOption.TopDirectoryOnly))
        {
            files++;
            bytes += file.Length;
        }

        return new FolderScan(files, bytes);
    }

    /// <summary>Counts the input documents of one date folder.</summary>
    public int CountFiles(DateFolder folder) => Scan(folder).Files;
}
