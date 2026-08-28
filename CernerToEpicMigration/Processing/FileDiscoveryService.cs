using System.IO.Enumeration;
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
        {
            WarnAboutStrandedFiles(basePath);
            return folders;
        }

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

    /// <summary>
    /// Warns about input files sitting directly in <c>InputBasePath</c> when date folders exist.
    /// </summary>
    /// <remarks>
    /// The flat-drop fallback above only applies when there are no date folders at all, so once
    /// one exists these files are skipped - counted nowhere, converted never, and the run still
    /// reports COMPLETED. That silent skip is the failure this warning exists to make loud; it
    /// does not move them, because a file at the root belongs to no date folder and there is
    /// nowhere correct to put it.
    /// </remarks>
    private void WarnAboutStrandedFiles(string basePath)
    {
        try
        {
            int stranded = Directory
                .EnumerateFiles(basePath, _config.Processing.FileSearchPattern, SearchOption.TopDirectoryOnly)
                .Take(1)
                .Count();

            if (stranded == 0)
                return;

            _logger.LogWarning(
                "Input file(s) matching {Pattern} sit directly in {BasePath} alongside the date folders. " +
                "They are NOT processed - documents belong in {BasePath}\\<date>\\ (optionally under a " +
                "patient sub-folder). Move them into a date folder to include them.",
                _config.Processing.FileSearchPattern, basePath, basePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not check {BasePath} for stranded input files.", basePath);
        }
    }

    /// <summary>Streams the input documents of one date folder, including patient sub-folders.</summary>
    public IEnumerable<string> EnumerateFiles(DateFolder folder) =>
        Walk(folder, _config.Processing.FileSearchPattern).Select(file => file.FullName);

    /// <summary>
    /// Walks one date folder, returning the files that match <paramref name="pattern"/> at any
    /// depth beneath it.
    /// </summary>
    /// <remarks>
    /// Both layouts the extract produces are the same walk: <c>{date}\doc.xhtml</c> is a tree with
    /// no sub-folders, and <c>{date}\patient_001\doc.xhtml</c> is one with them. Nothing here
    /// needs to know which it is looking at.
    /// <para>
    /// The reason this is a hand-rolled walk rather than <see cref="SearchOption.AllDirectories"/>
    /// is <c>archive</c> and <c>error</c>. Those are outputs, not input, and with
    /// <c>ArchiveOnSuccess</c> on the archive accumulates every document the migration has ever
    /// converted - so a blanket recursion would re-enumerate the entire archive on every scan of
    /// every folder, and a filter applied afterwards would still have paid to walk it. Skipping
    /// the subtree costs nothing. They are excluded only directly under the date folder, which is
    /// the only place they are created: a patient folder that happens to be called
    /// <c>archive</c> is still processed, because silently skipping input is the one behaviour
    /// this method must never have.
    /// </para>
    /// </remarks>
    private static IEnumerable<FileInfo> Walk(DateFolder folder, string pattern)
    {
        DirectoryInfo root = new(folder.Path);

        Stack<(DirectoryInfo Directory, bool IsDateFolder)> pending = new();
        pending.Push((root, true));

        while (pending.Count > 0)
        {
            (DirectoryInfo current, bool isDateFolder) = pending.Pop();

            foreach (FileInfo file in current.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                yield return file;

            foreach (DirectoryInfo child in current.EnumerateDirectories())
            {
                if (isDateFolder && ReservedFolderNames.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                pending.Push((child, false));
            }
        }
    }

    /// <summary>
    /// Streams the files of a date folder that <see cref="ProcessingOptions.FileSearchPattern"/>
    /// does <em>not</em> match - a <c>.pdf</c>, a <c>.txt</c>, an <c>.xhtml.bak</c> - so they can
    /// be quarantined rather than left behind.
    /// </summary>
    /// <remarks>
    /// Until now these files were invisible: discovery filtered them out, so they were never
    /// counted, never converted and never reported, and a date folder could be signed off as
    /// complete while documents sat in it that nothing had ever looked at. Enumerating them is
    /// the point of this method; deciding what to do with them belongs to the pipeline.
    /// <para>
    /// The match is done with <see cref="FileSystemName.MatchesSimpleExpression"/>, the same
    /// matcher <see cref="Directory.EnumerateFiles(string, string)"/> uses, so a file is treated
    /// as unmatched here exactly when the input enumeration skipped it. Testing each name as it
    /// streams past costs no memory, which matters: the alternative - listing both sets and
    /// subtracting - would hold a second copy of a million-entry folder listing.
    /// </para>
    /// </remarks>
    public IEnumerable<string> EnumerateUnmatchedFiles(DateFolder folder)
    {
        string pattern = _config.Processing.FileSearchPattern;

        foreach (FileInfo file in Walk(folder, "*"))
        {
            if (!FileSystemName.MatchesSimpleExpression(pattern, file.Name, ignoreCase: true))
                yield return file.FullName;
        }
    }

    /// <summary>
    /// Counts the input documents of one date folder and totals their size, without
    /// materialising the list. File sizes come from the directory entry itself, so this
    /// costs no more than counting.
    /// </summary>
    public FolderScan Scan(DateFolder folder)
    {
        int files = 0;
        long bytes = 0;

        foreach (FileInfo file in Walk(folder, _config.Processing.FileSearchPattern))
        {
            files++;
            bytes += file.Length;
        }

        return new FolderScan(files, bytes);
    }

    /// <summary>Counts the input documents of one date folder.</summary>
    public int CountFiles(DateFolder folder) => Scan(folder).Files;
}
