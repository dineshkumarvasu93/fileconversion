using CernerToEpicMigration.Cli;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Monitoring;
using CernerToEpicMigration.Processing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CernerToEpicMigration.Reporting;
using CernerToEpicMigration.Startup;
using CernerToEpicMigration.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace CernerToEpicMigration;

/// <summary>
/// Stage 1 of the Cerner to Epic document migration: convert Cerner XHTML documents
/// to RTF. Stage 2 (RTF to HL7) is deliberately not part of this build - see section
/// 17 of the design document for why it is still blocked.
/// </summary>
public static class Program
{
    private static class ExitCodes
    {
        public const int Success = 0;
        public const int CompletedWithFailures = 1;
        public const int InvalidInput = 2;
        public const int Fatal = 3;
    }

    public static async Task<int> Main(string[] args)
    {
        CommandLineOptions cli = CommandLineOptions.Parse(args);

        if (cli.ShowHelp)
        {
            CommandLineOptions.PrintUsage();
            return ExitCodes.Success;
        }

        if (cli.Errors.Count > 0)
        {
            foreach (string error in cli.Errors)
                Console.Error.WriteLine($"ERROR: {error}");

            Console.Error.WriteLine();
            CommandLineOptions.PrintUsage();
            return ExitCodes.InvalidInput;
        }

        MigrationConfig config;
        try
        {
            config = LoadConfiguration(cli);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: configuration could not be loaded. {exception.Message}");
            return ExitCodes.InvalidInput;
        }

        IReadOnlyList<string> configErrors = config.Validate();
        if (configErrors.Count > 0)
        {
            Console.Error.WriteLine("ERROR: invalid configuration.");
            foreach (string error in configErrors)
                Console.Error.WriteLine($"  - {error}");

            return ExitCodes.InvalidInput;
        }

        // windows-1252 and the other legacy code pages a Cerner export may declare.
        XhtmlDocumentReader.RegisterLegacyCodePages();

        ConfigureSerilog(config);

        try
        {
            await using ServiceProvider services = BuildServiceProvider(config);
            PrintBanner(config, cli);
            LogEnvironment(config, cli);

            using RunLock? runLock = RunLock.TryAcquire(config.ReportBasePath, out string? lockReason);
            if (runLock is null)
            {
                Log.Error("Refusing to start: {Reason}", lockReason);
                Console.Error.WriteLine($"ERROR: {lockReason}");
                return ExitCodes.InvalidInput;
            }

            IReadOnlyList<string> preflightProblems = services.GetRequiredService<PreflightCheck>().Run(cli.DryRun);
            if (preflightProblems.Count > 0)
            {
                Console.Error.WriteLine("ERROR: pre-flight checks failed.");
                foreach (string problem in preflightProblems)
                {
                    Log.Error("Pre-flight check failed: {Problem}", problem);
                    Console.Error.WriteLine($"  - {problem}");
                }

                return ExitCodes.InvalidInput;
            }

            Stage1Pipeline pipeline = services.GetRequiredService<Stage1Pipeline>();

            if (cli.DryRun)
                return RunDryRun(pipeline, cli);

            return await RunMigrationAsync(services, config, cli).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "The migration run terminated unexpectedly.");
            Console.Error.WriteLine($"FATAL: {exception.Message}");
            return ExitCodes.Fatal;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static MigrationConfig LoadConfiguration(CommandLineOptions cli)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        MigrationConfig config = configuration.GetSection(MigrationConfig.SectionName).Get<MigrationConfig>()
            ?? new MigrationConfig();

        if (cli.InputPath is not null)
            config.InputBasePath = cli.InputPath;

        if (cli.OutputPath is not null)
            config.OutputRtfBasePath = cli.OutputPath;

        if (cli.Threads is int threads)
            config.Processing.MaxDegreeOfParallelism = threads;

        if (cli.BatchSize is int batchSize)
            config.Processing.BatchSize = batchSize;

        return config;
    }

    private const string LogOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Two sinks over the same events, split by level.
    /// </summary>
    /// <remarks>
    /// The run log is written per day and rolled again at
    /// <see cref="MigrationConfig.LogFileSizeLimitMb"/>, so the files stay a size an operator can
    /// actually open: Serilog's own default is a 1 GB cap with rolling off, which makes the log go
    /// silent mid-run rather than rolling, and a bulk run that starts failing writes a stack trace
    /// per document and reaches that in a single day.
    /// <para>
    /// The second sink is the point of this method. Reviewing failures in the full log means
    /// finding a few thousand warnings among millions of progress lines; <c>errors_{date}.log</c>
    /// holds warnings and above only, so it is small enough to read end to end and pairs with the
    /// error report - the report says which documents failed, this says what the run was doing
    /// around them.
    /// </para>
    /// </remarks>
    private static void ConfigureSerilog(MigrationConfig config)
    {
        string logFolder = Path.GetFullPath(config.LogBasePath);
        Directory.CreateDirectory(logFolder);

        long sizeLimitBytes = config.LogFileSizeLimitMb * 1024L * 1024L;

        LoggerConfiguration logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logFolder, "migration_.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: sizeLimitBytes,
                retainedFileCountLimit: config.LogRetainedFileCountLimit,
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: LogOutputTemplate);

        if (config.EnableSeparateErrorLog)
        {
            logger = logger.WriteTo.File(
                Path.Combine(logFolder, "errors_.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: sizeLimitBytes,
                retainedFileCountLimit: config.LogRetainedFileCountLimit,
                restrictedToMinimumLevel: LogEventLevel.Warning,
                outputTemplate: LogOutputTemplate);
        }

        Log.Logger = logger.CreateLogger();
    }

    private static ServiceProvider BuildServiceProvider(MigrationConfig config)
    {
        ServiceCollection services = new();

        services.AddSingleton(config);
        services.AddSingleton(config.Processing);
        services.AddSingleton(config.Dashboard);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });

        services.AddSingleton<PreflightCheck>();
        services.AddSingleton<FileDiscoveryService>();
        services.AddSingleton<FileManager>();
        services.AddSingleton<IXhtmlToRtfConverter, TelerikXhtmlToRtfConverter>();
        services.AddSingleton<MetricsCollector>();
        services.AddSingleton<ConsoleDashboard>();
        services.AddSingleton<ReportWriter>();
        // Shares the report timestamp so the trace file lines up by name with the run that wrote it.
        services.AddSingleton(provider => new FileTraceWriter(
            config, provider.GetRequiredService<ReportWriter>().Timestamp));
        services.AddSingleton<CheckpointService>();
        services.AddSingleton<Stage1Pipeline>();

        return services.BuildServiceProvider();
    }

    private static int RunDryRun(Stage1Pipeline pipeline, CommandLineOptions cli)
    {
        IReadOnlyList<(DateFolder Folder, FolderScan Scan)> scan = pipeline.Scan(cli.DateFolder);
        int totalFiles = scan.Sum(entry => entry.Scan.Files);
        long totalBytes = scan.Sum(entry => entry.Scan.Bytes);

        Console.WriteLine("DRY RUN - nothing is converted, moved or written.");
        Console.WriteLine();
        Console.WriteLine($"{"Date Folder",-24}{"Files",12}{"Size (GB)",14}");
        Console.WriteLine(new string('-', 50));

        foreach ((DateFolder folder, FolderScan folderScan) in scan)
            Console.WriteLine($"{folder.Name,-24}{folderScan.Files,12:N0}{Gigabytes(folderScan.Bytes),14:N2}");

        Console.WriteLine(new string('-', 50));
        Console.WriteLine($"{"TOTAL",-24}{totalFiles,12:N0}{Gigabytes(totalBytes),14:N2}");
        Console.WriteLine();

        Log.Information("Dry run listed {Folders} folder(s), {Files:N0} file(s), {Gigabytes:N2} GB.",
            scan.Count, totalFiles, Gigabytes(totalBytes));

        return ExitCodes.Success;
    }

    private static double Gigabytes(long bytes) => bytes / (1024d * 1024d * 1024d);

    private static async Task<int> RunMigrationAsync(
        ServiceProvider services, MigrationConfig config, CommandLineOptions cli)
    {
        Stage1Pipeline pipeline = services.GetRequiredService<Stage1Pipeline>();
        MetricsCollector metrics = services.GetRequiredService<MetricsCollector>();
        ConsoleDashboard dashboard = services.GetRequiredService<ConsoleDashboard>();
        ReportWriter reportWriter = services.GetRequiredService<ReportWriter>();
        FileTraceWriter traceWriter = services.GetRequiredService<FileTraceWriter>();

        using CancellationTokenSource shutdownCts = new();

        void RequestShutdown(string trigger)
        {
            if (shutdownCts.IsCancellationRequested)
                return;

            Log.Warning("{Trigger} received; finishing in-flight files and stopping.", trigger);
            shutdownCts.Cancel();
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestShutdown("Ctrl+C");
        };

        // A service stop or `kill` on the migration server must shut down as cleanly as Ctrl+C:
        // the checkpoint and reports are written either way.
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            RequestShutdown("SIGTERM");
        });

        using CancellationTokenSource dashboardCts = new();
        dashboard.Start(dashboardCts.Token);

        Stage1Result result;
        try
        {
            result = await pipeline.RunAsync(new RunOptions(cli.DateFolder, cli.Resume), shutdownCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            dashboardCts.Cancel();
        }

        string status = result.FatalError is not null
            ? "FATAL"
            : result.Cancelled ? "STOPPED" : "COMPLETED";

        await dashboard.StopAsync(status).ConfigureAwait(false);

        reportWriter.WriteSummary(metrics, status);
        reportWriter.Dispose();
        traceWriter.Dispose();

        Log.Information(
            "Stage 1 {Status}: {Succeeded:N0} succeeded, {Failed:N0} failed, {Retries:N0} retr(y/ies), elapsed {Elapsed:hh\\:mm\\:ss}.",
            status, metrics.Succeeded, metrics.Failed, metrics.Retries, metrics.Elapsed);

        // Configured vs observed, in the run log as well as the summary CSV, because this is the
        // line that says whether the parallelism setting did anything.
        Log.Information(
            "Workers: {Configured} configured, {Peak} peak concurrent, {Average:F2} average ({Utilisation:F0}% occupied), "
            + "{ServiceTime:F1} ms per document per worker.",
            config.Processing.EffectiveParallelism,
            metrics.PeakActiveWorkers,
            metrics.AverageConcurrency,
            config.Processing.EffectiveParallelism <= 0
                ? 0
                : metrics.AverageConcurrency / config.Processing.EffectiveParallelism * 100d,
            metrics.AverageServiceTimeMs);

        // One line per worker slot, so an unattended run has the same breakdown the summary CSV
        // gets without anyone having to open the CSV.
        foreach (WorkerStatistics worker in metrics.GetWorkerStatistics())
        {
            Log.Information(
                "Worker {Slot}: {Processed:N0} document(s) ({Succeeded:N0} succeeded, {Failed:N0} failed), "
                + "busy {Busy:hh\\:mm\\:ss}, {AverageMs:F1} ms per document.",
                worker.Slot, worker.Processed, worker.Succeeded, worker.Failed, worker.Busy,
                worker.AverageMillisecondsPerFile);
        }

        LogPhaseSplit(metrics);

        PrintFinalSummary(config, metrics, reportWriter, traceWriter, status, result);

        if (result.FatalError is not null)
            return ExitCodes.Fatal;

        return metrics.Failed > 0 || result.Cancelled ? ExitCodes.CompletedWithFailures : ExitCodes.Success;
    }

    /// <summary>
    /// Writes the facts a support engineer needs when they open the log of a run that
    /// happened three days ago on a server they cannot see.
    /// </summary>
    private static void LogEnvironment(MigrationConfig config, CommandLineOptions cli)
    {
        Log.Information(
            "Cerner to Epic migration Stage 1 v{Version} starting on {Machine} ({OS}, {Cores} core(s), .NET {Runtime}).",
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment.MachineName,
            Environment.OSVersion.VersionString,
            Environment.ProcessorCount,
            Environment.Version);

        Log.Information(
            "Input={Input} OutputRtf={Output} Reports={Reports} Pattern={Pattern} Threads={Threads} " +
            "BatchSize={BatchSize} MaxAttempts={MaxAttempts} RetryDelayMs={RetryDelay} Timeout={Timeout}s " +
            "Archive={Archive} Overwrite={Overwrite} Base64Output={Base64Output} " +
            "DateFolder={DateFolder} Resume={Resume} DryRun={DryRun}",
            Path.GetFullPath(config.InputBasePath), Path.GetFullPath(config.OutputRtfBasePath),
            Path.GetFullPath(config.ReportBasePath), config.Processing.FileSearchPattern,
            config.Processing.EffectiveParallelism, config.Processing.BatchSize, config.Processing.MaxRetryCount,
            config.Processing.RetryDelayMs, config.Processing.ConversionTimeoutSeconds,
            config.Processing.ArchiveOnSuccess, config.Processing.OverwriteExistingRtf,
            config.Processing.EncodeRtfOutputAsBase64,
            cli.DateFolder ?? "(all)", cli.Resume, cli.DryRun);
    }

    private static void PrintBanner(MigrationConfig config, CommandLineOptions cli)
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        Console.WriteLine("+==============================================================================+");
        Console.WriteLine("|            CERNER TO EPIC MIGRATION - STAGE 1: XHTML -> RTF                  |");
        Console.WriteLine("+==============================================================================+");
        Console.WriteLine($"  Version    : {version}   Host: {Environment.MachineName}   .NET {Environment.Version}");
        Console.WriteLine($"  Input      : {Path.GetFullPath(config.InputBasePath)}");
        Console.WriteLine($"  RTF output : {Path.GetFullPath(config.OutputRtfBasePath)}");
        Console.WriteLine($"  Reports    : {Path.GetFullPath(config.ReportBasePath)}");
        Console.WriteLine($"  Logs       : {Path.GetFullPath(config.LogBasePath)}");
        Console.WriteLine($"  Threads    : {config.Processing.EffectiveParallelism}" +
                          $"   Batch size: {config.Processing.BatchSize}" +
                          $"   Max attempts: {config.Processing.MaxRetryCount}");

        if (config.Processing.EncodeRtfOutputAsBase64)
            Console.WriteLine("  RTF format : Base64-encoded (.rtf files hold an envelope, not plain RTF)");

        if (cli.DateFolder is not null)
            Console.WriteLine($"  Date folder: {cli.DateFolder} (single folder run)");

        if (cli.Resume)
            Console.WriteLine("  Resume     : enabled (completed folders are skipped)");

        Console.WriteLine();
    }

    private static void PrintFinalSummary(
        MigrationConfig config,
        MetricsCollector metrics,
        ReportWriter reportWriter,
        FileTraceWriter traceWriter,
        string status,
        Stage1Result result)
    {
        Console.WriteLine();
        Console.WriteLine($"Status            : {status}");
        Console.WriteLine($"Files found       : {metrics.TotalFound:N0}");
        Console.WriteLine($"Files converted   : {metrics.Succeeded:N0}");
        Console.WriteLine($"Files failed      : {metrics.Failed:N0}");
        Console.WriteLine($"Retries           : {metrics.Retries:N0}");
        Console.WriteLine($"Elapsed           : {metrics.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Throughput        : {metrics.FilesPerSecond:N1} files/s ({metrics.FilesPerSecond * 3600:N0} files/hour)");
        Console.WriteLine(
            $"Workers           : {config.Processing.EffectiveParallelism} configured, " +
            $"{metrics.PeakActiveWorkers} peak, {metrics.AverageConcurrency:N2} average, " +
            $"{metrics.AverageServiceTimeMs:N1} ms/document each");

        foreach (WorkerStatistics worker in metrics.GetWorkerStatistics())
        {
            Console.WriteLine(
                $"  worker {worker.Slot,-4}      : {worker.Processed,10:N0} document(s), " +
                $"busy {worker.Busy:hh\\:mm\\:ss}, {worker.AverageMillisecondsPerFile:N1} ms each");
        }

        WritePhaseLine(metrics);

        Console.WriteLine($"Summary report    : {reportWriter.SummaryReportPath}");

        if (reportWriter.HasErrorReport)
        {
            Console.WriteLine(
                $"Error report      : {reportWriter.ErrorReportPath}" +
                $"  ({reportWriter.ErrorRowCount:N0} row(s){DescribeParts(reportWriter.ErrorReportPaths.Count)})");
        }

        if (traceWriter.HasTrace)
        {
            Console.WriteLine(
                $"File trace        : {traceWriter.TracePath}" +
                $"  ({traceWriter.RowCount:N0} row(s){DescribeParts(traceWriter.TracePaths.Count)})");
        }

        Console.WriteLine($"Run log           : {Path.GetFullPath(config.LogBasePath)}\\migration_<date>.log");

        if (config.EnableSeparateErrorLog)
            Console.WriteLine($"Error log         : {Path.GetFullPath(config.LogBasePath)}\\errors_<date>.log");

        if (metrics.Failed > 0)
            Console.WriteLine($"Failed documents  : {Path.GetFullPath(config.InputBasePath)}\\<date>\\{FileManager.ErrorFolderName}");

        if (result.Cancelled)
            Console.WriteLine("Run was interrupted - restart with --resume to continue where it stopped.");

        if (result.FatalError is not null)
            Console.WriteLine($"Fatal error       : {result.FatalError.Message}");

        Console.WriteLine();
    }

    /// <summary>
    /// Names the extra part files of a split report, so nobody reads part 1 and stops. Silent when
    /// there is only one part.
    /// </summary>
    /// <summary>
    /// The CPU-versus-disk split of a run, on one console line.
    /// </summary>
    /// <remarks>
    /// This is the line a tuning run is read for. Throughput says how fast the run was and the
    /// worker figures say whether the threads were busy; neither says what they were busy on, and
    /// without that "adding threads did not help" has no follow-up action. The split does: time in
    /// Telerik points at cores, time in the file system points at the volume, the directories
    /// every worker shares, or an on-access scanner. Per-phase detail is in the summary CSV.
    /// </remarks>
    private static void WritePhaseLine(MetricsCollector metrics)
    {
        ConversionPhases phases = metrics.PhaseTotals;
        if (phases.TotalTicks == 0)
            return;

        (double cpuPercent, double diskPercent, double cpuMs, double diskMs) = SplitPhases(phases, metrics.Succeeded);

        Console.WriteLine(
            $"Time per document : {cpuMs:N1} ms CPU ({cpuPercent:N0}%), {diskMs:N1} ms disk ({diskPercent:N0}%)");
    }

    /// <summary>The same split in the run log, for an unattended run that has no console.</summary>
    private static void LogPhaseSplit(MetricsCollector metrics)
    {
        ConversionPhases phases = metrics.PhaseTotals;
        if (phases.TotalTicks == 0)
            return;

        (double cpuPercent, double diskPercent, double cpuMs, double diskMs) = SplitPhases(phases, metrics.Succeeded);

        Log.Information(
            "Phase split per document: {CpuMs:F1} ms CPU ({CpuPercent:F0}%) - decode {DecodeMs:F1}, "
            + "import {ImportMs:F1}, export {ExportMs:F1}; {DiskMs:F1} ms disk ({DiskPercent:F0}%) - "
            + "read {ReadMs:F1}, write {WriteMs:F1}, archive {ArchiveMs:F1}.",
            cpuMs, cpuPercent,
            PerFileMs(phases.DecodeTicks + phases.ValidateTicks, metrics.Succeeded),
            PerFileMs(phases.ImportTicks, metrics.Succeeded),
            PerFileMs(phases.ExportTicks, metrics.Succeeded),
            diskMs, diskPercent,
            PerFileMs(phases.ReadTicks, metrics.Succeeded),
            PerFileMs(phases.WriteTicks, metrics.Succeeded),
            PerFileMs(phases.ArchiveTicks, metrics.Succeeded));
    }

    /// <summary>Splits the phase totals into the CPU half and the file system half.</summary>
    private static (double CpuPercent, double DiskPercent, double CpuMs, double DiskMs) SplitPhases(
        ConversionPhases phases, long converted)
    {
        long cpuTicks = phases.DecodeTicks + phases.ValidateTicks + phases.ImportTicks + phases.ExportTicks;
        long diskTicks = phases.ReadTicks + phases.WriteTicks + phases.ArchiveTicks;
        long total = cpuTicks + diskTicks;

        return (
            total == 0 ? 0 : cpuTicks * 100d / total,
            total == 0 ? 0 : diskTicks * 100d / total,
            PerFileMs(cpuTicks, converted),
            PerFileMs(diskTicks, converted));
    }

    private static double PerFileMs(long ticks, long files) =>
        files <= 0 ? 0 : ticks * 1000d / Stopwatch.Frequency / files;

    private static string DescribeParts(int partCount) =>
        partCount > 1 ? $" in {partCount} parts, _001 to _{partCount:D3}" : string.Empty;
}
