using CernerToEpicMigration.Cli;
using Xunit;

namespace CernerToEpicMigration.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void Options_are_parsed_from_separate_arguments()
    {
        CommandLineOptions options = CommandLineOptions.Parse(
            ["--input", @"D:\In", "--output", @"D:\Out", "--threads", "16", "--batch-size", "500", "--resume"]);

        Assert.Empty(options.Errors);
        Assert.Equal(@"D:\In", options.InputPath);
        Assert.Equal(@"D:\Out", options.OutputPath);
        Assert.Equal(16, options.Threads);
        Assert.Equal(500, options.BatchSize);
        Assert.True(options.Resume);
        Assert.False(options.DryRun);
    }

    [Fact]
    public void Options_are_parsed_from_the_inline_equals_form()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--input=D:\\In", "--threads=4"]);

        Assert.Empty(options.Errors);
        Assert.Equal(@"D:\In", options.InputPath);
        Assert.Equal(4, options.Threads);
    }

    [Fact]
    public void Stage_2_is_refused_because_it_is_not_implemented()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--stage", "2"]);

        Assert.Contains(options.Errors, error => error.Contains("Stage 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Stage_1_is_accepted()
    {
        Assert.Empty(CommandLineOptions.Parse(["--stage", "1"]).Errors);
    }

    [Fact]
    public void A_missing_value_is_reported_instead_of_swallowing_the_next_flag()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--input", "--resume"]);

        Assert.Contains(options.Errors, error => error.Contains("--input requires a value", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_numeric_thread_count_is_reported()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--threads", "many"]);

        Assert.Contains(options.Errors, error => error.Contains("expects a number", StringComparison.Ordinal));
    }

    [Fact]
    public void A_zero_thread_count_is_reported()
    {
        Assert.NotEmpty(CommandLineOptions.Parse(["--threads", "0"]).Errors);
    }

    [Fact]
    public void An_unknown_argument_is_reported_rather_than_ignored()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--parallelism", "8"]);

        Assert.Contains(options.Errors, error => error.Contains("Unknown argument", StringComparison.Ordinal));
    }

    [Fact]
    public void Date_folder_must_be_a_folder_name_not_a_path()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--date-folder", @"D:\Migration\Input\2026-08-01"]);

        Assert.Contains(options.Errors, error => error.Contains("folder name", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_is_recognised()
    {
        Assert.True(CommandLineOptions.Parse(["--help"]).ShowHelp);
    }
}
