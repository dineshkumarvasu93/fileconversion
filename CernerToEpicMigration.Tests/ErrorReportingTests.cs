using System.Text;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Processing;
using CernerToEpicMigration.Reporting;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// The error report is what an operator reads instead of walking dozens of <c>error</c> folders,
/// so these tests are about whether a row is enough to act on: does it name the batch, the log
/// file, the original name and where the document went, and does everything that fails end up in
/// it - including the files the old build filtered out and never mentioned.
/// </summary>
public class ErrorReportingTests
{
    [Fact]
    public async Task Files_that_do_not_match_the_search_pattern_are_quarantined_and_reported()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 2);

        // The three shapes this actually takes in a Cerner drop: another format, a mistyped
        // extension, and a half-renamed file.
        File.WriteAllText(Path.Combine(folder.Path, "scan_9.pdf"), "%PDF-1.4");
        File.WriteAllText(Path.Combine(folder.Path, "doc_3.xhtm"), "typo");
        File.WriteAllText(Path.Combine(folder.Path, "doc_4.xhtml.bak"), "backup");

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, workspace.Metrics.Succeeded);
        Assert.Equal(3, workspace.Metrics.Failed);

        // Quarantined files are counted in before they are failed, so the summary arithmetic
        // still adds up rather than reporting more processed than found.
        Assert.Equal(5, workspace.Metrics.TotalFound);

        string[] errorFiles = workspace.ErrorFiles("2026-08-01").Select(Path.GetFileName).ToArray()!;
        Assert.Contains("scan_9.pdf", errorFiles);
        Assert.Contains("doc_3.xhtm", errorFiles);
        Assert.Contains("doc_4.xhtml.bak", errorFiles);

        reportWriter.Flush();
        string report = TempWorkspace.ReadShared(reportWriter.ErrorReportPath);
        Assert.Contains("scan_9.pdf", report, StringComparison.Ordinal);
        Assert.Contains("File name does not match *.xhtml", report, StringComparison.Ordinal);
        Assert.Contains(nameof(FailureSource.Discovery), report, StringComparison.Ordinal);

        // They never belonged to a batch, and the report says so rather than inventing one.
        Assert.Contains($",{WorkItem.ScanBatchId},", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quarantining_can_be_switched_off_for_folders_another_process_writes_to()
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.QuarantineUnmatchedFiles = false;
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        File.WriteAllText(Path.Combine(folder.Path, "extract.tmp"), "still being written");

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(0, workspace.Metrics.Failed);
        Assert.True(File.Exists(Path.Combine(folder.Path, "extract.tmp")));
    }

    [Theory]
    [InlineData("   \r\n  ", "whitespace only")]
    [InlineData("Progress note: patient stable. No markup here.", "text with no elements")]
    public async Task A_file_with_no_xhtml_in_it_is_moved_to_the_error_folder(string payload, string why)
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 2);

        // A well-formed envelope around something that is not a document. Telerik would import
        // this without complaint and export an empty RTF, which every report would call a success.
        TempWorkspace.WriteInput(Path.Combine(folder.Path, "doc_1.xhtml"), payload);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, workspace.Metrics.Failed);
        Assert.Equal(1, workspace.Metrics.Succeeded);

        string[] errorFiles = workspace.ErrorFiles("2026-08-01").Select(Path.GetFileName).ToArray()!;
        Assert.Contains("doc_1.xhtml", errorFiles);
        Assert.Contains("doc_1.error.log", errorFiles);

        // No RTF was written for it - the point is that it never reaches the converter.
        Assert.Single(workspace.OutputFiles("2026-08-01"));

        reportWriter.Flush();
        string report = TempWorkspace.ReadShared(reportWriter.ErrorReportPath);
        Assert.Contains("The file holds no XHTML document", report, StringComparison.Ordinal);
        Assert.Contains(nameof(NotXhtmlContentException), report, StringComparison.Ordinal);
        Assert.True(why.Length > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("     ")]
    [InlineData("plain text, no tags")]
    [InlineData("2 < 3 and 4 > 1")]
    public void The_content_gate_rejects_a_payload_with_no_markup_in_it(string payload)
    {
        // An empty payload never reaches this in the pipeline - the Base64 decoder rejects an
        // empty file first - but the gate has to hold on its own for the callers that do.
        Assert.Throws<NotXhtmlContentException>(() => XhtmlContentValidator.Validate(payload, "doc_1.xhtml"));
    }

    [Theory]
    [InlineData("<html><body><p>Note</p></body></html>")]
    [InlineData("<p>A fragment, which is what a Cerner export often is</p>")]
    [InlineData("<?xml version=\"1.0\"?><ns:note xmlns:ns=\"urn:x\">text</ns:note>")]
    [InlineData("Leading prose, then <div>markup</div>")]
    public void The_content_gate_passes_anything_with_an_element_in_it(string payload)
    {
        // Deliberately permissive: rejecting a valid clinical fragment is far worse than passing
        // an odd one through, so the gate asks for markup, not for good markup.
        Assert.Null(Record.Exception(() => XhtmlContentValidator.Validate(payload, "doc_1.xhtml")));
    }

    [Fact]
    public async Task Binary_content_in_a_valid_envelope_is_rejected_rather_than_converted()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);

        byte[] binary = new byte[512];
        Random.Shared.NextBytes(binary);
        for (int index = 0; index < binary.Length; index += 4)
            binary[index] = 0;

        File.WriteAllText(
            Path.Combine(folder.Path, "doc_1.xhtml"), Convert.ToBase64String(binary), Encoding.ASCII);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(1, workspace.Metrics.Failed);
        Assert.Empty(workspace.OutputFiles("2026-08-01"));
    }

    [Fact]
    public async Task Content_validation_can_be_switched_off()
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.ValidateXhtmlContent = false;
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        TempWorkspace.WriteInput(Path.Combine(folder.Path, "doc_1.xhtml"), "no markup at all");

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        // Back to the old behaviour: Telerik accepts it and an RTF is produced.
        Assert.Equal(0, workspace.Metrics.Failed);
        Assert.Single(workspace.OutputFiles("2026-08-01"));
    }

    [Fact]
    public void The_error_report_splits_into_numbered_parts()
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.MaxReportRowsPerFile = 2;
        using ReportWriter writer = workspace.CreateReportWriter();

        for (int index = 1; index <= 5; index++)
        {
            writer.RecordFailure(new FileFailure(
                $"doc_{index}.xhtml", "2026-08-01", ErrorCategory.Permanent, "XmlException",
                "invalid", 1, DateTimeOffset.UtcNow, null));
        }

        writer.Flush();

        Assert.Equal(5, writer.ErrorRowCount);
        Assert.Equal(3, writer.ErrorReportPaths.Count);
        Assert.EndsWith("_001.csv", writer.ErrorReportPath, StringComparison.Ordinal);
        Assert.EndsWith("_003.csv", writer.ErrorReportPaths[^1], StringComparison.Ordinal);

        // Every part is independently usable: header, then its own rows. A reviewer opening
        // part 2 must not have to go back to part 1 to know what the columns are.
        foreach (string part in writer.ErrorReportPaths)
        {
            string[] lines = TempWorkspace.ReadSharedLines(part);
            Assert.StartsWith("Row,Error Time Utc,Batch Id", lines[0].TrimStart('﻿'), StringComparison.Ordinal);
            Assert.InRange(lines.Length - 1, 1, 2);
        }

        // The row numbers run across the parts, so "row 4" identifies one failure in the run.
        string lastPart = TempWorkspace.ReadShared(writer.ErrorReportPaths[^1]);
        Assert.Contains("\n5,", lastPart, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_trace_carries_the_batch_id_that_joins_it_to_the_error_report()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 4);
        workspace.Config.Processing.BatchSize = 2;
        workspace.Config.Processing.EnableFileTrace = true;

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        using FileTraceWriter traceWriter = workspace.CreateTraceWriter(reportWriter);
        await workspace.CreatePipeline(reportWriter, new InstrumentedConverter(), traceWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        traceWriter.Flush();

        string[] lines = TempWorkspace.ReadSharedLines(traceWriter.TracePath);
        Assert.Contains("Batch Id", lines[0], StringComparison.Ordinal);

        string[] batchIds = lines.Skip(1).Select(line => line.Split(',')[3]).Distinct().Order().ToArray();
        Assert.Contains("Sub Folder", lines[0], StringComparison.Ordinal);
        Assert.Equal(new[] { "2026-08-01#0001", "2026-08-01#0002" }, batchIds);
    }

    [Fact]
    public void The_trace_splits_into_parts_on_the_same_setting_as_the_error_report()
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.MaxReportRowsPerFile = 3;
        workspace.Config.Processing.EnableFileTrace = true;

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        using FileTraceWriter traceWriter = workspace.CreateTraceWriter(reportWriter);

        for (int index = 0; index < 7; index++)
        {
            traceWriter.Record(new FileTrace(
                WorkerSlot: 1,
                ThreadId: 1,
                DateFolder: "2026-08-01",
                BatchId: "2026-08-01#0001",
                FileName: $"doc_{index}.xhtml",
                SubFolder: "",
                StartUtc: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromMilliseconds(5),
                Attempts: 1,
                Outcome: "Succeeded"));
        }

        traceWriter.Flush();

        Assert.Equal(7, traceWriter.RowCount);
        Assert.Equal(3, traceWriter.TracePaths.Count);
    }

    [Fact]
    public void An_error_row_names_the_log_file_and_where_the_document_ended_up()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);

        // A name already taken in the error folder, so the move has to suffix it - the one case
        // where the name in the error folder and the name the file arrived with differ, and the
        // reason the report carries both.
        string errorFolder = Path.Combine(folder.Path, FileManager.ErrorFolderName);
        Directory.CreateDirectory(errorFolder);
        File.WriteAllText(Path.Combine(errorFolder, "doc_1.xhtml"), "an earlier run left this");

        WorkItem item = new(Path.Combine(folder.Path, "doc_1.xhtml"), folder, "2026-08-01#0001");
        FileFailure failure = new(
            item.FilePath, folder.Name, ErrorCategory.Permanent, "XmlException", "invalid",
            2, DateTimeOffset.UtcNow, null, item.BatchId);

        ErrorPlacement placement = workspace.CreateFileManager().MoveToError(item, failure);

        Assert.Equal("doc_1_1.xhtml", placement.FileName);
        Assert.Equal("doc_1_1.error.log", placement.ErrorLogFileName);

        using ReportWriter writer = workspace.CreateReportWriter();
        writer.RecordFailure(failure with
        {
            ErrorFilePath = placement.ErrorFilePath,
            ErrorLogFileName = placement.ErrorLogFileName,
            MoveError = placement.MoveError
        });
        writer.Flush();

        string[] row = TempWorkspace.ReadSharedLines(writer.ErrorReportPath)[1].Split(',');
        Assert.Equal("2026-08-01#0001", row[2]);
        Assert.Equal("doc_1_1.xhtml", row[5]);   // File Name - what it is called now
        Assert.Equal("doc_1.xhtml", row[6]);     // Actual File Name - what it arrived as
        Assert.Equal("doc_1_1.error.log", row[12]);
        Assert.Equal("Yes", row[13]);
    }
}
