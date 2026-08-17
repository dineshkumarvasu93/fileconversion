using System.Text;
using System.Text.Json;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Processing;
using CernerToEpicMigration.Reporting;
using CernerToEpicMigration.State;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>End-to-end runs of Stage 1 over real folders, with the real converter.</summary>
public class Stage1PipelineTests
{
    [Fact]
    public async Task Every_document_is_converted_archived_and_counted()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 3);
        workspace.AddDateFolder("2026-08-02", 2);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(5, workspace.Metrics.Succeeded);
        Assert.Equal(0, workspace.Metrics.Failed);
        Assert.Equal(3, workspace.OutputFiles("2026-08-01").Length);
        Assert.Equal(2, workspace.OutputFiles("2026-08-02").Length);
        Assert.Equal(3, workspace.ArchivedFiles("2026-08-01").Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace.InputPath, "2026-08-01")));
    }

    [Fact]
    public async Task Only_the_requested_date_folder_is_processed()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 2);
        workspace.AddDateFolder("2026-08-02", 2);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: "2026-08-02", Resume: false), CancellationToken.None);

        Assert.Empty(workspace.OutputFiles("2026-08-01"));
        Assert.Equal(2, workspace.OutputFiles("2026-08-02").Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(workspace.InputPath, "2026-08-01")).Length);
    }

    [Fact]
    public async Task A_failing_document_is_retried_then_moved_to_the_error_folder()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);

        // Refusing to overwrite an existing RTF gives a deterministic, repeatable failure.
        workspace.Config.Processing.OverwriteExistingRtf = false;
        Directory.CreateDirectory(Path.Combine(workspace.OutputPath, folder.Name));
        File.WriteAllText(Path.Combine(workspace.OutputPath, folder.Name, "doc_1.rtf"), "existing", Encoding.UTF8);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, workspace.Metrics.Failed);
        Assert.Equal(0, workspace.Metrics.Succeeded);
        Assert.Equal(2, workspace.ErrorFiles("2026-08-01").Length); // the document and its .error.log
        Assert.True(File.Exists(reportWriter.ErrorReportPath));
        Assert.Contains("doc_1.xhtml", TempWorkspace.ReadShared(reportWriter.ErrorReportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_that_cannot_be_base64_decoded_is_moved_to_the_error_folder()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 2);

        // One file arrives without its Base64 envelope; the other is a normal document.
        File.WriteAllText(
            Path.Combine(folder.Path, "doc_1.xhtml"), TempWorkspace.SampleXhtml, Encoding.UTF8);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, workspace.Metrics.Failed);
        Assert.Equal(1, workspace.Metrics.Succeeded);
        Assert.Single(workspace.OutputFiles("2026-08-01"));

        string errorLog = workspace.ErrorFiles("2026-08-01").Single(path => path.EndsWith(".error.log", StringComparison.Ordinal));
        string log = TempWorkspace.ReadShared(errorLog);
        Assert.Contains(nameof(Base64DecodingException), log, StringComparison.Ordinal);
        Assert.Contains("Category: Permanent", log, StringComparison.Ordinal);
        Assert.Contains("Attempts: 1", log, StringComparison.Ordinal);  // permanent, so never retried

        // The unreadable file itself moved out of the input folder, the good one was archived.
        Assert.Contains(workspace.ErrorFiles("2026-08-01"), path => path.EndsWith("doc_1.xhtml", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task A_checkpoint_records_the_completed_folders()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 2);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        string checkpointPath = Path.Combine(workspace.ReportPath, "checkpoint.json");
        Assert.True(File.Exists(checkpointPath));

        Checkpoint checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(checkpointPath))!;
        Assert.Contains("2026-08-01", checkpoint.CompletedFolders);
        Assert.Equal(2, checkpoint.TotalSucceeded);
    }

    [Fact]
    public async Task Resuming_skips_the_folders_the_checkpoint_marks_as_complete()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 2);
        workspace.AddDateFolder("2026-08-02", 2);

        using ReportWriter firstRun = workspace.CreateReportWriter();
        await workspace.CreatePipeline(firstRun)
            .RunAsync(new RunOptions(DateFolder: "2026-08-01", Resume: false), CancellationToken.None);

        TempWorkspace resumed = workspace;
        using ReportWriter secondRun = resumed.CreateReportWriter();
        await resumed.CreatePipeline(secondRun)
            .RunAsync(new RunOptions(DateFolder: null, Resume: true), CancellationToken.None);

        // 2026-08-01 was already complete, so only the second folder is processed on resume.
        Assert.Equal(2, workspace.OutputFiles("2026-08-01").Length);
        Assert.Equal(2, workspace.OutputFiles("2026-08-02").Length);
    }

    [Fact]
    public async Task A_run_that_is_cancelled_before_it_starts_converts_nothing()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 3);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), cancelled.Token);

        Assert.False(result.Completed);
        Assert.True(result.Cancelled);
        Assert.Empty(workspace.OutputFiles("2026-08-01"));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(workspace.InputPath, "2026-08-01")).Length);
    }

    [Fact]
    public async Task An_empty_input_tree_completes_without_error()
    {
        using TempWorkspace workspace = new();

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(0, workspace.Metrics.Succeeded);
    }
}

