using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Reporting;
using CernerToEpicMigration.Startup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CernerToEpicMigration.Tests;

public class ReportWriterTests
{
    [Fact]
    public void Error_rows_use_the_required_columns_and_are_visible_while_the_writer_is_open()
    {
        using TempWorkspace workspace = new();
        using ReportWriter writer = workspace.CreateReportWriter();
        string filePath = Path.Combine(workspace.InputPath, "2026-08-01", "doc_1.xhtml");

        writer.RecordFailure(new FileFailure(
            filePath, "2026-08-01", ErrorCategory.Permanent, "XmlException",
            "Invalid element, line 42, column 7", 3, DateTimeOffset.UtcNow, null));
        writer.Flush();

        string[] lines = TempWorkspace.ReadSharedLines(writer.ErrorReportPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("File Path,Error Category,Error Message", lines[0].TrimStart('\uFEFF'));
        Assert.Contains(filePath, lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",Permanent,\"Invalid element, line 42, column 7\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_and_newlines_are_preserved_and_escaped()
    {
        using TempWorkspace workspace = new();
        using ReportWriter writer = workspace.CreateReportWriter();
        string message = "Unexpected \"tag\"\r\non line 2";

        writer.RecordFailure(new FileFailure(
            "doc_1.xhtml", "2026-08-01", ErrorCategory.Permanent, "XmlException",
            message, 1, DateTimeOffset.UtcNow, null));
        writer.Flush();

        string report = TempWorkspace.ReadShared(writer.ErrorReportPath);
        Assert.Contains("\"Unexpected \"\"tag\"\"\r\non line 2\"", report, StringComparison.Ordinal);
    }

    [Fact]
    public void No_error_report_is_created_when_nothing_fails()
    {
        using TempWorkspace workspace = new();
        using ReportWriter writer = workspace.CreateReportWriter();

        Assert.False(writer.HasErrorReport);
        Assert.False(File.Exists(writer.ErrorReportPath));
    }

    [Fact]
    public void The_summary_reports_counts_as_plain_numbers_that_a_spreadsheet_can_read()
    {
        using TempWorkspace workspace = new();
        using ReportWriter writer = workspace.CreateReportWriter();

        workspace.Metrics.RegisterFolder("2026-08-01", 2500);
        workspace.Metrics.Start();
        workspace.Metrics.RecordSuccess("2026-08-01", 1024, 2048);
        workspace.Metrics.Stop();

        writer.WriteSummary(workspace.Metrics, "COMPLETED");

        string report = File.ReadAllText(writer.SummaryReportPath);
        Assert.True(writer.HasSummaryReport);
        Assert.Contains("Total Files Found,2500", report, StringComparison.Ordinal);
        Assert.Contains("Total Files Succeeded,1", report, StringComparison.Ordinal);
        Assert.Contains("Status,COMPLETED", report, StringComparison.Ordinal);
        Assert.Contains("--- Per-Folder Breakdown ---", report, StringComparison.Ordinal);
        Assert.Contains("2026-08-01,2500,1,0", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Average Files Per Hour,1,", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_failures_are_logged_and_do_not_escape()
    {
        using TempWorkspace workspace = new();
        string fileInsteadOfFolder = Path.Combine(workspace.Root, "not-a-folder");
        File.WriteAllText(fileInsteadOfFolder, "occupied");
        workspace.Config.ReportBasePath = fileInsteadOfFolder;
        TestLogger<ReportWriter> logger = new();
        using ReportWriter writer = new(workspace.Config, logger);
        FileFailure failure = new(
            "doc_1.xhtml", "2026-08-01", ErrorCategory.Permanent, "XmlException",
            "invalid", 1, DateTimeOffset.UtcNow, null);

        Exception? errorFailure = Record.Exception(() => writer.RecordFailure(failure));
        Exception? summaryFailure = Record.Exception(() => writer.WriteSummary(workspace.Metrics, "COMPLETED"));

        Assert.Null(errorFailure);
        Assert.Null(summaryFailure);
        Assert.False(writer.HasErrorReport);
        Assert.False(writer.HasSummaryReport);
        Assert.Equal(2, logger.ErrorCount);
}

    private sealed class TestLogger<T> : ILogger<T>
    {
        public int ErrorCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                ErrorCount++;
        }
    }
}

public class MigrationConfigTests
{
    [Fact]
    public void A_complete_configuration_validates()
    {
        using TempWorkspace workspace = new();

        Assert.Empty(workspace.Config.Validate());
    }

    [Fact]
    public void A_missing_input_folder_is_reported()
    {
        using TempWorkspace workspace = new();
        workspace.Config.InputBasePath = Path.Combine(workspace.Root, "no-such-folder");

        Assert.Contains(workspace.Config.Validate(), error => error.Contains("InputBasePath", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_batch_size_is_reported(int batchSize)
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.BatchSize = batchSize;

        Assert.Contains(workspace.Config.Validate(), error => error.Contains("BatchSize", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_log_retention_limit_is_reported(int retentionLimit)
    {
        using TempWorkspace workspace = new();
        workspace.Config.LogRetainedFileCountLimit = retentionLimit;

        Assert.Contains(workspace.Config.Validate(), error => error.Contains("LogRetainedFileCountLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void A_negative_error_log_cap_is_reported()
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.MaxErrorLogFiles = -1;

        Assert.Contains(workspace.Config.Validate(), error => error.Contains("MaxErrorLogFiles", StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_parallelism_means_auto_detect_not_zero_threads()
    {
        ProcessingOptions options = new() { MaxDegreeOfParallelism = 0 };

        Assert.Equal(Environment.ProcessorCount, options.EffectiveParallelism);
    }

    [Fact]
    public void An_explicit_thread_count_is_used_as_given()
    {
        ProcessingOptions options = new() { MaxDegreeOfParallelism = 4 };

        Assert.Equal(4, options.EffectiveParallelism);
    }
}

public class RunLockTests
{
    [Fact]
    public void A_second_run_against_the_same_report_folder_is_refused()
    {
        using TempWorkspace workspace = new();

        using RunLock? first = RunLock.TryAcquire(workspace.ReportPath, out string? firstReason);
        Assert.NotNull(first);
        Assert.Null(firstReason);

        using RunLock? second = RunLock.TryAcquire(workspace.ReportPath, out string? secondReason);
        Assert.Null(second);
        Assert.Contains("Another migration run", secondReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lock_is_released_and_removed_when_the_run_ends()
    {
        using TempWorkspace workspace = new();

        using (RunLock? first = RunLock.TryAcquire(workspace.ReportPath, out _))
        {
            Assert.NotNull(first);
            Assert.True(File.Exists(Path.Combine(workspace.ReportPath, RunLock.LockFileName)));
        }

        Assert.False(File.Exists(Path.Combine(workspace.ReportPath, RunLock.LockFileName)));

        using RunLock? afterRelease = RunLock.TryAcquire(workspace.ReportPath, out _);
        Assert.NotNull(afterRelease);
    }
}

public class PreflightCheckTests
{
    [Fact]
    public void A_healthy_environment_passes_including_the_telerik_smoke_test()
    {
        using TempWorkspace workspace = new();
        PreflightCheck preflight = new(
            workspace.Config,
            workspace.CreateConverter(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PreflightCheck>.Instance);

        Assert.Empty(preflight.Run(dryRun: false));
    }

    [Fact]
    public void An_unreadable_input_folder_is_reported_before_any_work_starts()
    {
        using TempWorkspace workspace = new();
        workspace.Config.InputBasePath = Path.Combine(workspace.Root, "missing");
        PreflightCheck preflight = new(
            workspace.Config,
            workspace.CreateConverter(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PreflightCheck>.Instance);

        Assert.Contains(preflight.Run(dryRun: false), problem => problem.Contains("InputBasePath", StringComparison.Ordinal));
    }
}

