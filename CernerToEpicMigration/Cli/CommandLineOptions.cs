namespace CernerToEpicMigration.Cli;

/// <summary>
/// Command-line arguments described in design document section 13.2.
/// Values supplied here override the matching appsettings.json values.
/// </summary>
public sealed class CommandLineOptions
{
    public string? InputPath { get; private set; }

    public string? OutputPath { get; private set; }

    public int? Threads { get; private set; }

    public int? BatchSize { get; private set; }

    /// <summary>Requested stage. Only "1" is supported by this build.</summary>
    public string Stage { get; private set; } = "1";

    /// <summary>Restrict the run to a single date folder.</summary>
    public string? DateFolder { get; private set; }

    /// <summary>Scan and report only - nothing is converted, moved or written.</summary>
    public bool DryRun { get; private set; }

    /// <summary>Skip date folders already recorded as complete in checkpoint.json.</summary>
    public bool Resume { get; private set; }

    public bool ShowHelp { get; private set; }

    public List<string> Errors { get; } = new();

    public static CommandLineOptions Parse(string[] args)
    {
        CommandLineOptions options = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string name = arg;
            string? inlineValue = null;

            int separator = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && separator > 0)
            {
                name = arg[..separator];
                inlineValue = arg[(separator + 1)..];
            }

            switch (name.ToLowerInvariant())
            {
                case "--input":
                    options.InputPath = ReadValue(args, ref i, inlineValue, name, options.Errors);
                    break;
                case "--output":
                    options.OutputPath = ReadValue(args, ref i, inlineValue, name, options.Errors);
                    break;
                case "--threads":
                    options.Threads = ReadInt(args, ref i, inlineValue, name, options.Errors);
                    break;
                case "--batch-size":
                    options.BatchSize = ReadInt(args, ref i, inlineValue, name, options.Errors);
                    break;
                case "--stage":
                    options.Stage = ReadValue(args, ref i, inlineValue, name, options.Errors) ?? options.Stage;
                    break;
                case "--date-folder":
                    options.DateFolder = ReadValue(args, ref i, inlineValue, name, options.Errors);
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--resume":
                    options.Resume = true;
                    break;
                case "--help":
                case "-h":
                case "-?":
                    options.ShowHelp = true;
                    break;
                default:
                    options.Errors.Add($"Unknown argument: {arg}");
                    break;
            }
        }

        options.ValidateValues();
        return options;
    }

    private void ValidateValues()
    {
        if (Threads is <= 0)
            Errors.Add("--threads must be greater than zero.");

        if (BatchSize is <= 0)
            Errors.Add("--batch-size must be greater than zero.");

        if (!string.Equals(Stage, "1", StringComparison.OrdinalIgnoreCase))
        {
            Errors.Add(
                $"--stage {Stage} is not available. This build implements Stage 1 (XHTML -> RTF) only; " +
                "Stage 2 (RTF -> HL7) is not part of it.");
        }

        if (DateFolder is not null && DateFolder.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            Errors.Add($"--date-folder must be a folder name, not a path: {DateFolder}");
    }

    private static string? ReadValue(string[] args, ref int index, string? inlineValue, string name, List<string> errors)
    {
        if (inlineValue is not null)
        {
            if (inlineValue.Length == 0)
            {
                errors.Add($"{name} requires a value.");
                return null;
            }

            return inlineValue;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            errors.Add($"{name} requires a value.");
            return null;
        }

        return args[++index];
    }

    private static int? ReadInt(string[] args, ref int index, string? inlineValue, string name, List<string> errors)
    {
        string? raw = ReadValue(args, ref index, inlineValue, name, errors);
        if (raw is null)
            return null;

        if (!int.TryParse(raw, out int value))
        {
            errors.Add($"{name} expects a number but got '{raw}'.");
            return null;
        }

        return value;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            CernerToEpicMigration - Stage 1: Cerner XHTML to RTF conversion

            Usage:
              CernerToEpicMigration.exe [options]

            Options:
              --input <path>          Override the input base path (folder of date-wise folders)
              --output <path>         Override the RTF output base path
              --threads <count>       Override max parallelism (default: processor count)
              --batch-size <size>     Override batch size (default: 1000)
              --stage <1>             Stage to run. Only stage 1 is implemented in this build
              --date-folder <name>    Process a single date folder only, e.g. 2026-08-01
              --dry-run               Scan and report without converting or moving anything
              --resume                Skip date folders already completed per checkpoint.json
              --help                  Display this help

            Exit codes:
              0  All discovered files converted successfully (or nothing to do)
              1  Completed, but one or more files failed and were moved to the error folder
              2  Invalid arguments or configuration
              3  Fatal error - processing stopped early
            """);
    }
}
