using CernerToEpicMigration.Models;
using CernerToEpicMigration.Processing;
using CernerToEpicMigration.Reporting;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// The extract arrives in one of two shapes - <c>{date}\doc.xhtml</c> or
/// <c>{date}\patient_001\doc.xhtml</c> - and both have to work without a switch.
/// </summary>
/// <remarks>
/// The nested shape used to be skipped in silence: discovery looked at the top level of the date
/// folder only, so the documents were never counted, never converted, never reported, and the run
/// still printed COMPLETED. These tests exist mostly to keep that from coming back, and to pin
/// down the thing that makes nesting more than a discovery change - the patient folder has to be
/// carried into the output, the archive and the error folder, because flattening it loses which
/// patient a document belongs to.
/// </remarks>
public class PatientFolderTests
{
    /// <summary>Writes a convertible document at <paramref name="relativePath"/> under a date folder.</summary>
    private static void AddDocument(DateFolder folder, string relativePath)
    {
        string path = Path.Combine(folder.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TempWorkspace.WriteInput(path, TempWorkspace.SampleXhtml);
    }

    private static string[] AllFilesUnder(string path) =>
        Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task Documents_in_patient_sub_folders_are_converted_and_the_structure_is_mirrored()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = new("2026-08-01", Path.Combine(workspace.InputPath, "2026-08-01"));
        AddDocument(folder, Path.Combine("patient_001", "doc_a.xhtml"));
        AddDocument(folder, Path.Combine("patient_002", "doc_b.xhtml"));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, workspace.Metrics.TotalFound);
        Assert.Equal(2, workspace.Metrics.Succeeded);

        // The patient folder survives into the output rather than being flattened away.
        Assert.True(File.Exists(Path.Combine(workspace.OutputPath, "2026-08-01", "patient_001", "doc_a.rtf")));
        Assert.True(File.Exists(Path.Combine(workspace.OutputPath, "2026-08-01", "patient_002", "doc_b.rtf")));

        // ...and into the archive.
        Assert.True(File.Exists(Path.Combine(
            folder.Path, FileManager.ArchiveFolderName, "patient_001", "doc_a.xhtml")));
        Assert.True(File.Exists(Path.Combine(
            folder.Path, FileManager.ArchiveFolderName, "patient_002", "doc_b.xhtml")));
    }

    [Fact]
    public async Task The_flat_layout_still_works_alongside_the_nested_one()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);   // doc_1.xhtml at the top
        AddDocument(folder, Path.Combine("patient_001", "doc_a.xhtml"));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(2, workspace.Metrics.Succeeded);
        Assert.True(File.Exists(Path.Combine(workspace.OutputPath, "2026-08-01", "doc_1.rtf")));
        Assert.True(File.Exists(Path.Combine(workspace.OutputPath, "2026-08-01", "patient_001", "doc_a.rtf")));
    }

    [Fact]
    public async Task Two_patients_with_the_same_document_name_do_not_collide()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = new("2026-08-01", Path.Combine(workspace.InputPath, "2026-08-01"));

        // The realistic case, and the reason the sub-folder is mirrored instead of flattened:
        // an extract that numbers documents per patient produces this constantly.
        AddDocument(folder, Path.Combine("patient_001", "doc_1.xhtml"));
        AddDocument(folder, Path.Combine("patient_002", "doc_1.xhtml"));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(2, workspace.Metrics.Succeeded);

        // Two separate outputs under their own patients - not one file, and not doc_1_1.rtf,
        // which would have severed the link between the document and the patient.
        Assert.True(File.Exists(Path.Combine(workspace.OutputPath, "2026-08-01", "patient_001", "doc_1.rtf")));
        Assert.True(File.Exists(Path.Combine(workspace.OutputPath, "2026-08-01", "patient_002", "doc_1.rtf")));
        Assert.Equal(2, AllFilesUnder(Path.Combine(workspace.OutputPath, "2026-08-01")).Length);
    }

    [Fact]
    public async Task A_failure_inside_a_patient_folder_keeps_its_patient_in_the_error_folder_and_the_report()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = new("2026-08-01", Path.Combine(workspace.InputPath, "2026-08-01"));
        AddDocument(folder, Path.Combine("patient_001", "doc_a.xhtml"));

        string bad = Path.Combine(folder.Path, "patient_002", "doc_b.xhtml");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        TempWorkspace.WriteInput(bad, "no markup in here at all");

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(1, workspace.Metrics.Succeeded);
        Assert.Equal(1, workspace.Metrics.Failed);

        // The failed document and its log stay under the patient they belong to.
        string errorPatient = Path.Combine(folder.Path, FileManager.ErrorFolderName, "patient_002");
        Assert.True(File.Exists(Path.Combine(errorPatient, "doc_b.xhtml")));
        Assert.True(File.Exists(Path.Combine(errorPatient, "doc_b.error.log")));

        reportWriter.Flush();
        string[] row = TempWorkspace.ReadSharedLines(reportWriter.ErrorReportPath)[1].Split(',');
        Assert.Equal("2026-08-01", row[3]);
        Assert.Equal("patient_002", row[4]);   // Sub Folder - what makes the row actionable
        Assert.Equal("doc_b.xhtml", row[5]);
    }

    [Fact]
    public async Task A_second_run_does_not_reprocess_the_archive_or_error_folders()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = new("2026-08-01", Path.Combine(workspace.InputPath, "2026-08-01"));
        AddDocument(folder, Path.Combine("patient_001", "doc_a.xhtml"));

        string bad = Path.Combine(folder.Path, "patient_001", "doc_bad.xhtml");
        TempWorkspace.WriteInput(bad, "no markup");

        using (ReportWriter first = workspace.CreateReportWriter())
        {
            await workspace.CreatePipeline(first)
                .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);
        }

        Assert.Equal(1, workspace.Metrics.Succeeded);
        Assert.Equal(1, workspace.Metrics.Failed);

        // Everything has now moved into archive\patient_001 and error\patient_001. Recursion must
        // not treat those as input, or the second run would convert the archive all over again -
        // and with ArchiveOnSuccess on, that archive grows to hold the entire migration.
        long succeededAfterFirstRun = workspace.Metrics.Succeeded;
        long failedAfterFirstRun = workspace.Metrics.Failed;

        using ReportWriter second = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(second)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        // Nothing left to do: the counters have not moved and no new failure was recorded.
        Assert.True(result.Completed);
        Assert.Equal(succeededAfterFirstRun, workspace.Metrics.Succeeded);
        Assert.Equal(failedAfterFirstRun, workspace.Metrics.Failed);
        Assert.False(second.HasErrorReport);
    }

    [Fact]
    public void The_scan_counts_documents_in_patient_folders()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = new("2026-08-01", Path.Combine(workspace.InputPath, "2026-08-01"));
        AddDocument(folder, "doc_top.xhtml");
        AddDocument(folder, Path.Combine("patient_001", "doc_a.xhtml"));
        AddDocument(folder, Path.Combine("patient_002", "doc_b.xhtml"));

        FileDiscoveryService discovery = new(
            workspace.Config, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileDiscoveryService>.Instance);

        FolderScan scan = discovery.Scan(folder);

        // --dry-run reports this number, so it has to agree with what a real run will process.
        Assert.Equal(3, scan.Files);
        Assert.Equal(3, discovery.EnumerateFiles(folder).Count());
    }

    [Fact]
    public void Unmatched_files_are_found_inside_patient_folders_too()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = new("2026-08-01", Path.Combine(workspace.InputPath, "2026-08-01"));
        AddDocument(folder, Path.Combine("patient_001", "doc_a.xhtml"));
        File.WriteAllText(Path.Combine(folder.Path, "patient_001", "scan.pdf"), "%PDF-1.4");

        FileDiscoveryService discovery = new(
            workspace.Config, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileDiscoveryService>.Instance);

        string[] unmatched = discovery.EnumerateUnmatchedFiles(folder).ToArray();

        Assert.Single(unmatched);
        Assert.EndsWith("scan.pdf", unmatched[0], StringComparison.Ordinal);
    }
}
