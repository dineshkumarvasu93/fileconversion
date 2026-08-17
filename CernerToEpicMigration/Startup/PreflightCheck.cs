using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Processing;
using Microsoft.Extensions.Logging;

namespace CernerToEpicMigration.Startup;

/// <summary>
/// Fails fast, before a million documents are touched, on the problems that would
/// otherwise surface one file at a time: unwritable folders, a broken Telerik licence,
/// or not enough disk space for the output.
/// </summary>
public sealed class PreflightCheck
{
    private const string SmokeTestXhtml =
        """<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><body><p>preflight</p></body></html>""";

    private readonly MigrationConfig _config;
    private readonly IXhtmlToRtfConverter _converter;
    private readonly ILogger<PreflightCheck> _logger;

    public PreflightCheck(MigrationConfig config, IXhtmlToRtfConverter converter, ILogger<PreflightCheck> logger)
    {
        _config = config;
        _converter = converter;
        _logger = logger;
    }

    /// <summary>
    /// Runs the checks that must pass before processing starts.
    /// An empty result means the run may proceed.
    /// </summary>
    public IReadOnlyList<string> Run(bool dryRun)
    {
        List<string> problems = new();

        CheckReadable(_config.InputBasePath, problems);
        CheckWritable(_config.ReportBasePath, "ReportBasePath", problems);
        CheckWritable(_config.LogBasePath, "LogBasePath", problems);

        if (!dryRun)
        {
            CheckWritable(_config.OutputRtfBasePath, "OutputRtfBasePath", problems);
            CheckConversionWorks(problems);
        }

        return problems;
    }

    private void CheckReadable(string path, List<string> problems)
    {
        try
        {
            Directory.EnumerateFileSystemEntries(Path.GetFullPath(path)).Take(1).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"InputBasePath cannot be read: {path} ({exception.Message})");
        }
    }

    private void CheckWritable(string path, string settingName, List<string> problems)
    {
        string fullPath = Path.GetFullPath(path);
        string probePath = Path.Combine(fullPath, $".write-probe-{Environment.ProcessId}.tmp");

        try
        {
            Directory.CreateDirectory(fullPath);
            File.WriteAllText(probePath, "preflight", Encoding.UTF8);
            File.Delete(probePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{settingName} is not writable: {fullPath} ({exception.Message})");
        }
    }

    /// <summary>
    /// Converts one tiny document end to end. A missing or expired Telerik licence, or a
    /// missing native dependency, shows up here rather than as a million failed files.
    /// </summary>
    private void CheckConversionWorks(List<string> problems)
    {
        string workingFolder = Path.Combine(Path.GetFullPath(_config.ReportBasePath), ".preflight");
        string inputPath = Path.Combine(workingFolder, $"preflight-{Environment.ProcessId}.xhtml");
        string outputPath = Path.ChangeExtension(inputPath, ".rtf");

        try
        {
            Directory.CreateDirectory(workingFolder);

            // Written through the same Base64 envelope as a real input document, so the
            // smoke test exercises the whole read path and not just the Telerik call.
            File.WriteAllText(
                inputPath,
                Convert.ToBase64String(new UTF8Encoding(false).GetBytes(SmokeTestXhtml)),
                Encoding.ASCII);

            ConversionOutcome outcome = _converter.Convert(inputPath, outputPath);
            if (outcome.OutputBytes == 0)
                problems.Add("The Telerik conversion smoke test produced an empty RTF file.");
            else
                _logger.LogInformation("Conversion smoke test passed ({Bytes} bytes of RTF).", outcome.OutputBytes);
        }
        catch (Exception exception)
        {
            problems.Add($"The Telerik conversion smoke test failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
            TryDeleteDirectory(workingFolder);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leftover preflight scratch files are harmless.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Same - not worth failing a migration over.
        }
    }
}
