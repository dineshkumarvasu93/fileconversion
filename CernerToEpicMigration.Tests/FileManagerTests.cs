using System.Text;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Processing;
using Xunit;

namespace CernerToEpicMigration.Tests;

public class FileManagerTests
{
    private static FileFailure Failure(string fileName) => new(
        FileName: fileName,
        DateFolder: "2026-08-01",
        Category: ErrorCategory.Permanent,
        ErrorType: "XmlException",
        ErrorMessage: "Invalid XHTML at line 42",
        Attempts: 3,
        TimestampUtc: DateTimeOffset.UtcNow,
        StackTrace: "at Telerik...");

    [Fact]
    public void The_rtf_output_path_mirrors_the_date_folder()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        WorkItem item = new(Path.Combine(folder.Path, "doc_1.xhtml"), folder);

        string output = workspace.CreateFileManager().GetRtfOutputPath(item);

        Assert.Equal(Path.Combine(workspace.OutputPath, "2026-08-01", "doc_1.rtf"), output);
    }

    [Fact]
    public void A_successful_document_is_moved_into_the_archive_folder()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        WorkItem item = new(Path.Combine(folder.Path, "doc_1.xhtml"), folder);

        workspace.CreateFileManager().ArchiveSuccess(item);

        Assert.False(File.Exists(item.FilePath));
        Assert.Single(workspace.ArchivedFiles("2026-08-01"));
    }

    [Fact]
    public void Archiving_is_skipped_when_it_is_switched_off()
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.ArchiveOnSuccess = false;
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        WorkItem item = new(Path.Combine(folder.Path, "doc_1.xhtml"), folder);

        workspace.CreateFileManager().ArchiveSuccess(item);

        Assert.True(File.Exists(item.FilePath));
    }

    [Fact]
    public void An_existing_archived_file_is_never_overwritten()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        string archiveFolder = Path.Combine(folder.Path, FileManager.ArchiveFolderName);
        Directory.CreateDirectory(archiveFolder);
        File.WriteAllText(Path.Combine(archiveFolder, "doc_1.xhtml"), "an earlier run", Encoding.UTF8);

        workspace.CreateFileManager().ArchiveSuccess(new WorkItem(Path.Combine(folder.Path, "doc_1.xhtml"), folder));

        string[] archived = workspace.ArchivedFiles("2026-08-01");
        Assert.Equal(2, archived.Length);
        Assert.Contains(archived, path => Path.GetFileName(path) == "doc_1_1.xhtml");
        Assert.Equal("an earlier run", File.ReadAllText(Path.Combine(archiveFolder, "doc_1.xhtml")));
    }

    [Fact]
    public void A_failed_document_is_moved_to_the_error_folder_with_its_log()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        WorkItem item = new(Path.Combine(folder.Path, "doc_1.xhtml"), folder);

        string? moveError = workspace.CreateFileManager().MoveToError(item, Failure("doc_1.xhtml"));

        Assert.Null(moveError);
        Assert.False(File.Exists(item.FilePath));

        string[] errorFiles = workspace.ErrorFiles("2026-08-01");
        Assert.Equal(2, errorFiles.Length);

        string log = File.ReadAllText(errorFiles.Single(path => path.EndsWith(".error.log", StringComparison.Ordinal)));
        Assert.Contains("Date Folder: 2026-08-01", log, StringComparison.Ordinal);
        Assert.Contains("Attempts: 3", log, StringComparison.Ordinal);
        Assert.Contains("Invalid XHTML at line 42", log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_that_cannot_be_moved_still_gets_an_error_log()
    {
        if (!OperatingSystem.IsWindows())
            return; // Only Windows enforces the exclusive share used to simulate the lock.

        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        WorkItem item = new(Path.Combine(folder.Path, "doc_1.xhtml"), folder);

        using (File.Open(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            string? moveError = workspace.CreateFileManager().MoveToError(item, Failure("doc_1.xhtml"));

            Assert.NotNull(moveError);
            Assert.True(File.Exists(item.FilePath));
        }

        string log = Path.Combine(folder.Path, FileManager.ErrorFolderName, "doc_1.error.log");
        Assert.True(File.Exists(log));
        Assert.Contains("left in the input folder", File.ReadAllText(log), StringComparison.Ordinal);
    }
}
