using CernerToEpicMigration.Cli;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Monitoring;
using CernerToEpicMigration.Processing;
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

    /// <summary>Size cap per log file. Serilog rolls to a new file rather than stopping at it.</summary>
    private const long LogFileSizeLimitBytes = 256L * 1024 * 1024;

    private static void ConfigureSerilog(MigrationConfig config)
    {
        Directory.CreateDirectory(Path.GetFullPath(config.LogBasePath));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(Path.GetFullPath(config.LogBasePath), "migration_.log"),
                rollingInterval: RollingInterval.Day,
                // Serilog's default is a 1 GB cap with rolling off, which makes the log go
                // silent mid-run instead of rolling. A bulk run that starts failing writes a
                // stack trace per file, so the cap is reachable on a single day.
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                retainedFileCountLimit: 30,
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
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

        Log.Information(
            "Stage 1 {Status}: {Succeeded:N0} succeeded, {Failed:N0} failed, {Retries:N0} retr(y/ies), elapsed {Elapsed:hh\\:mm\\:ss}.",
            status, metrics.Succeeded, metrics.Failed, metrics.Retries, metrics.Elapsed);

        PrintFinalSummary(config, metrics, reportWriter, status, result);

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
        Console.WriteLine($"Summary report    : {reportWriter.SummaryReportPath}");

        if (reportWriter.HasErrorReport)
            Console.WriteLine($"Error report      : {reportWriter.ErrorReportPath}");

        if (metrics.Failed > 0)
            Console.WriteLine($"Failed documents  : {Path.GetFullPath(config.InputBasePath)}\\<date>\\{FileManager.ErrorFolderName}");

        if (result.Cancelled)
            Console.WriteLine("Run was interrupted - restart with --resume to continue where it stopped.");

        if (result.FatalError is not null)
            Console.WriteLine($"Fatal error       : {result.FatalError.Message}");

        Console.WriteLine();
    }
}
