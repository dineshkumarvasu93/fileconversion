using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Monitoring;
using CernerToEpicMigration.Processing;
using CernerToEpicMigration.Reporting;
using CernerToEpicMigration.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// A throwaway input/output/report tree plus the wiring the tests need, so each test runs
/// against real folders and real files rather than mocks of the file system.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public const string SampleXhtml =
        """<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><body><h1>Progress Note</h1><p>Patient <strong>stable</strong>.</p></body></html>""";

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "c2e-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(InputPath);
        Directory.CreateDirectory(OutputPath);
        Directory.CreateDirectory(ReportPath);

        Config = new MigrationConfig
        {
            InputBasePath = InputPath,
            OutputRtfBasePath = OutputPath,
            ReportBasePath = ReportPath,
            LogBasePath = Path.Combine(Root, "Logs"),
            Processing = new ProcessingOptions
            {
                MaxDegreeOfParallelism = 2,
                BatchSize = 2,
                MaxRetryCount = 2,
                RetryDelayMs = 1,
                FileSearchPattern = "*.xhtml"
            },
            Dashboard = new DashboardOptions { EnableConsoleDashboard = false }
        };
    }

    public string Root { get; }

    public string InputPath => Path.Combine(Root, "Input");

    public string OutputPath => Path.Combine(Root, "Output", "rtf");

    public string ReportPath => Path.Combine(Root, "Reports");

    public MigrationConfig Config { get; }

    public MetricsCollector Metrics { get; } = new();

    /// <summary>Creates a date folder with <paramref name="count"/> convertible documents.</summary>
    public DateFolder AddDateFolder(string name, int count, string? content = null)
    {
        string path = Path.Combine(InputPath, name);
        Directory.CreateDirectory(path);

        for (int i = 1; i <= count; i++)
            WriteInput(Path.Combine(path, $"doc_{i}.xhtml"), content ?? SampleXhtml);

        return new DateFolder(name, path);
    }

    /// <summary>
    /// Writes an input document the way the extract delivers it: the XHTML encoded with
    /// <paramref name="encoding"/> and then wrapped in a Base64 envelope.
    /// </summary>
    public static void WriteInput(string path, string xhtml, Encoding? encoding = null) =>
        File.WriteAllText(path, Base64Envelope(xhtml, encoding), Encoding.ASCII);

    /// <summary>The Base64 envelope for an XHTML document.</summary>
    public static string Base64Envelope(string xhtml, Encoding? encoding = null) =>
        Convert.ToBase64String((encoding ?? new UTF8Encoding(false)).GetBytes(xhtml));

    public FileManager CreateFileManager() => new(Config);

    public TelerikXhtmlToRtfConverter CreateConverter() => new(Config.Processing);

    public ReportWriter CreateReportWriter() => new(Config);

    public Stage1Pipeline CreatePipeline(ReportWriter reportWriter) => new(
        Config,
        new FileDiscoveryService(Config, NullLogger<FileDiscoveryService>.Instance),
        CreateFileManager(),
        CreateConverter(),
        Metrics,
        reportWriter,
        new CheckpointService(Config, NullLogger<CheckpointService>.Instance),
        NullLogger<Stage1Pipeline>.Instance);

    public string[] OutputFiles(string dateFolder) =>
        Directory.Exists(Path.Combine(OutputPath, dateFolder))
            ? Directory.GetFiles(Path.Combine(OutputPath, dateFolder))
            : [];

    public string[] ArchivedFiles(string dateFolder) =>
        Files(Path.Combine(InputPath, dateFolder, FileManager.ArchiveFolderName));

    public string[] ErrorFiles(string dateFolder) =>
        Files(Path.Combine(InputPath, dateFolder, FileManager.ErrorFolderName));

    private static string[] Files(string path) => Directory.Exists(path) ? Directory.GetFiles(path) : [];

    /// <summary>Reads a file the migration may still have open for writing, as an operator would.</summary>
    public static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string[] ReadSharedLines(string path) =>
        ReadShared(path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temp folders are the operating system's problem after this point.
        }
    }
}
