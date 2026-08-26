using CernerToEpicMigration.Models;
using CernerToEpicMigration.Processing;
using CernerToEpicMigration.Reporting;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// The settings that govern a bulk run - how many workers, how many attempts, how big a batch -
/// asserted against an instrumented converter so the results are exact rather than timing-dependent.
/// </summary>
public class ConcurrencyAndRetryTests
{
    [Fact]
    public async Task Concurrency_never_exceeds_the_configured_thread_count()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 200);
        workspace.Config.Processing.MaxDegreeOfParallelism = 4;
        workspace.Config.Processing.BatchSize = 200;

        InstrumentedConverter converter = new(work: TimeSpan.FromMilliseconds(15));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.True(result.Completed);

        // The ceiling holds...
        Assert.True(
            converter.PeakConcurrency <= 4,
            $"peak concurrency was {converter.PeakConcurrency}, expected at most 4");
        Assert.True(
            workspace.Metrics.PeakActiveWorkers <= 4,
            $"metrics recorded a peak of {workspace.Metrics.PeakActiveWorkers}, expected at most 4");

        // ...and is actually reached, so a run that silently serialised would still fail here.
        Assert.Equal(4, converter.PeakConcurrency);
        Assert.Equal(4, workspace.Metrics.PeakActiveWorkers);
    }

    [Fact]
    public async Task A_single_thread_run_never_overlaps()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 20);
        workspace.Config.Processing.MaxDegreeOfParallelism = 1;

        InstrumentedConverter converter = new();

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(1, converter.PeakConcurrency);
        Assert.Equal(1, workspace.Metrics.PeakActiveWorkers);
    }

    [Fact]
    public async Task Every_worker_is_counted_in_the_average_concurrency()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 120);
        workspace.Config.Processing.MaxDegreeOfParallelism = 4;
        workspace.Config.Processing.BatchSize = 120;

        InstrumentedConverter converter = new(work: TimeSpan.FromMilliseconds(20));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        // Four workers each doing steady work: the average has to land well above one worker
        // and cannot exceed the configured ceiling. The band is deliberately loose - this
        // asserts the figure is computed from real occupancy, not that the CI agent is fast.
        Assert.InRange(workspace.Metrics.AverageConcurrency, 1.5, 4.0);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_up_to_the_configured_attempt_count()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        workspace.Config.Processing.MaxRetryCount = 3;
        workspace.Config.Processing.RetryDelayMs = 1;

        string target = Path.Combine(folder.Path, "doc_1.xhtml");
        InstrumentedConverter converter = new((_, _) => new IOException("file is locked"));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(3, converter.AttemptsFor(target));
        Assert.Equal(1, workspace.Metrics.Failed);
        Assert.Equal(2, workspace.Metrics.Retries);   // attempts minus the first one

        string errorLog = workspace.ErrorFiles("2026-08-01")
            .Single(path => path.EndsWith(".error.log", StringComparison.Ordinal));
        string log = TempWorkspace.ReadShared(errorLog);
        Assert.Contains("Category: Transient", log, StringComparison.Ordinal);
        Assert.Contains("Attempts: 3", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_failure_that_clears_before_the_attempts_run_out_succeeds()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 1);
        workspace.Config.Processing.MaxRetryCount = 3;
        workspace.Config.Processing.RetryDelayMs = 1;

        // Fails twice, then converts - the file-locked-by-the-extract case.
        InstrumentedConverter converter = new((_, attempt) =>
            attempt < 3 ? new IOException("file is locked") : null);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(1, workspace.Metrics.Succeeded);
        Assert.Equal(0, workspace.Metrics.Failed);
        Assert.Equal(2, workspace.Metrics.Retries);
        Assert.Single(workspace.OutputFiles("2026-08-01"));
        Assert.Empty(workspace.ErrorFiles("2026-08-01"));
    }

    [Fact]
    public async Task MaxRetryCount_of_one_means_a_single_attempt()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        workspace.Config.Processing.MaxRetryCount = 1;

        string target = Path.Combine(folder.Path, "doc_1.xhtml");
        InstrumentedConverter converter = new((_, _) => new IOException("file is locked"));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(1, converter.AttemptsFor(target));
        Assert.Equal(0, workspace.Metrics.Retries);
        Assert.Equal(1, workspace.Metrics.Failed);
    }

    [Fact]
    public async Task A_permanent_failure_is_never_retried()
    {
        using TempWorkspace workspace = new();
        DateFolder folder = workspace.AddDateFolder("2026-08-01", 1);
        workspace.Config.Processing.MaxRetryCount = 5;

        string target = Path.Combine(folder.Path, "doc_1.xhtml");
        InstrumentedConverter converter = new((_, _) => new NotSupportedException("unsupported markup"));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.Equal(1, converter.AttemptsFor(target));
        Assert.Equal(0, workspace.Metrics.Retries);

        string log = TempWorkspace.ReadShared(workspace.ErrorFiles("2026-08-01")
            .Single(path => path.EndsWith(".error.log", StringComparison.Ordinal)));
        Assert.Contains("Category: Permanent", log, StringComparison.Ordinal);
        Assert.Contains("Attempts: 1", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_retry_delay_grows_with_the_attempt_number()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 1);
        workspace.Config.Processing.MaxRetryCount = 4;
        workspace.Config.Processing.RetryDelayMs = 200;   // 200 + 400 + 600 = 1200 ms of backoff

        InstrumentedConverter converter = new((_, _) => new IOException("file is locked"), TimeSpan.Zero);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        Assert.Equal(4, workspace.Metrics.Retries + 1);
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(1200),
            $"backoff took {elapsed.TotalMilliseconds:F0} ms, expected at least 1200 ms");
    }

    [Fact]
    public async Task A_fatal_error_stops_the_run_without_retrying()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 50);
        workspace.Config.Processing.MaxDegreeOfParallelism = 2;
        workspace.Config.Processing.MaxRetryCount = 3;

        InstrumentedConverter converter = new((_, _) => new UnauthorizedAccessException("access denied"));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        Stage1Result result = await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.NotNull(result.FatalError);
        Assert.Equal(0, workspace.Metrics.Retries);

        // Stopped early rather than grinding through all 50.
        Assert.True(converter.TotalCalls < 50, $"{converter.TotalCalls} files were attempted; expected the run to stop early");
    }

    [Fact]
    public async Task A_batch_completes_before_the_next_one_starts()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 40);
        workspace.Config.Processing.MaxDegreeOfParallelism = 4;
        workspace.Config.Processing.BatchSize = 10;

        InstrumentedConverter converter = new(work: TimeSpan.FromMilliseconds(10));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        // Sort by start, then walk in groups of BatchSize: no call in a group may begin before
        // every call of the previous group has returned.
        var calls = converter.Calls.OrderBy(call => call.StartTicks).ToList();
        Assert.Equal(40, calls.Count);

        for (int batch = 1; batch * 10 < calls.Count; batch++)
        {
            long previousBatchEnd = calls.Take(batch * 10).Max(call => call.EndTicks);
            long nextBatchStart = calls.Skip(batch * 10).Min(call => call.StartTicks);

            Assert.True(
                nextBatchStart >= previousBatchEnd,
                $"batch {batch + 1} started before batch {batch} finished");
        }
    }

    [Fact]
    public async Task The_file_trace_records_one_row_per_document_with_a_bounded_worker_slot()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 60);
        workspace.Config.Processing.MaxDegreeOfParallelism = 3;
        workspace.Config.Processing.BatchSize = 60;
        workspace.Config.Processing.EnableFileTrace = true;

        InstrumentedConverter converter = new(work: TimeSpan.FromMilliseconds(10));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        using FileTraceWriter traceWriter = workspace.CreateTraceWriter(reportWriter);
        await workspace.CreatePipeline(reportWriter, converter, traceWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        traceWriter.Flush();

        string[] lines = TempWorkspace.ReadSharedLines(traceWriter.TracePath);
        Assert.StartsWith("Worker Slot,Thread Id,", lines[0], StringComparison.Ordinal);
        Assert.Equal(60, lines.Length - 1);

        int[] slots = lines.Skip(1)
            .Select(line => int.Parse(line.Split(',')[0]))
            .ToArray();

        // Slots are recycled, so they stay in a small range around the concurrency rather than
        // climbing with the file count - which is the whole reason they, and not thread ids,
        // are the worker identity in the trace.
        Assert.All(slots, slot => Assert.InRange(slot, 1, 3));
        Assert.Equal(3, slots.Distinct().Count());
    }

    [Fact]
    public async Task The_file_trace_is_not_written_unless_it_is_enabled()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 5);
        Assert.False(workspace.Config.Processing.EnableFileTrace);

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        using FileTraceWriter traceWriter = workspace.CreateTraceWriter(reportWriter);
        await workspace.CreatePipeline(reportWriter, new InstrumentedConverter(), traceWriter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        Assert.False(traceWriter.HasTrace);
        Assert.False(File.Exists(traceWriter.TracePath));
    }

    [Fact]
    public async Task The_summary_report_records_the_observed_concurrency_not_just_the_configured_one()
    {
        using TempWorkspace workspace = new();
        workspace.AddDateFolder("2026-08-01", 80);
        workspace.Config.Processing.MaxDegreeOfParallelism = 4;
        workspace.Config.Processing.BatchSize = 80;

        InstrumentedConverter converter = new(work: TimeSpan.FromMilliseconds(10));

        using ReportWriter reportWriter = workspace.CreateReportWriter();
        await workspace.CreatePipeline(reportWriter, converter)
            .RunAsync(new RunOptions(DateFolder: null, Resume: false), CancellationToken.None);

        reportWriter.WriteSummary(workspace.Metrics, "COMPLETED");
        string summary = TempWorkspace.ReadShared(reportWriter.SummaryReportPath);

        Assert.Contains("Threads,4", summary, StringComparison.Ordinal);
        Assert.Contains("Peak Concurrent Workers,4", summary, StringComparison.Ordinal);
        Assert.Contains("Average Concurrent Workers,", summary, StringComparison.Ordinal);
        Assert.Contains("Worker Utilisation (%),", summary, StringComparison.Ordinal);
    }
}
