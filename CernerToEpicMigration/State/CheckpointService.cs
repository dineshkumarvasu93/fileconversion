using System.Text.Json;
using CernerToEpicMigration.Configuration;
using Microsoft.Extensions.Logging;

namespace CernerToEpicMigration.State;

/// <summary>Contents of checkpoint.json (design document section 14.3).</summary>
public sealed class Checkpoint
{
    public string? LastProcessedFolder { get; set; }

    public string? LastProcessedFile { get; set; }

    public long TotalProcessed { get; set; }

    public long TotalSucceeded { get; set; }

    public long TotalFailed { get; set; }

    public DateTimeOffset LastCheckpointTime { get; set; }

    /// <summary>Date folders finished end to end; skipped when the run is resumed.</summary>
    public List<string> CompletedFolders { get; set; } = new();
}

/// <summary>
/// Reads and writes the checkpoint file. A checkpoint is saved after every batch, so a
/// killed run can be restarted with --resume without redoing completed date folders.
/// </summary>
public sealed class CheckpointService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly MigrationConfig _config;
    private readonly ILogger<CheckpointService> _logger;
    private readonly object _writeLock = new();

    public CheckpointService(MigrationConfig config, ILogger<CheckpointService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string CheckpointPath => Path.Combine(Path.GetFullPath(_config.ReportBasePath), "checkpoint.json");

    public Checkpoint Load()
    {
        if (!File.Exists(CheckpointPath))
        {
            _logger.LogWarning("No checkpoint found at {Path}; starting from the beginning.", CheckpointPath);
            return new Checkpoint();
        }

        try
        {
            Checkpoint? checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(CheckpointPath));
            if (checkpoint is null)
                return new Checkpoint();

            _logger.LogInformation(
                "Resuming from checkpoint written {Time:u}: {Processed} files processed, {Folders} folder(s) complete.",
                checkpoint.LastCheckpointTime, checkpoint.TotalProcessed, checkpoint.CompletedFolders.Count);

            return checkpoint;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Checkpoint at {Path} could not be read; starting from the beginning.", CheckpointPath);
            return new Checkpoint();
        }
    }

    /// <summary>
    /// Moves an existing checkpoint aside before a run that starts from scratch. Without this,
    /// forgetting --resume after a crash would overwrite the record of what had been done.
    /// </summary>
    public void ArchiveExisting()
    {
        if (!File.Exists(CheckpointPath))
            return;

        string archivedPath = Path.Combine(
            Path.GetDirectoryName(CheckpointPath)!,
            $"checkpoint_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        try
        {
            File.Move(CheckpointPath, archivedPath, overwrite: true);
            _logger.LogInformation(
                "This run does not use --resume; the previous checkpoint was kept as {Path}.", archivedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The previous checkpoint could not be archived; it will be overwritten.");
        }
    }

    public void Save(Checkpoint checkpoint)
    {
        checkpoint.LastCheckpointTime = DateTimeOffset.UtcNow;

        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetFullPath(_config.ReportBasePath));

                // Write-then-replace so a crash cannot leave a truncated checkpoint.
                string tempPath = CheckpointPath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(checkpoint, SerializerOptions));
                File.Move(tempPath, CheckpointPath, overwrite: true);
            }
        }
        catch (Exception exception)
        {
            // A failed checkpoint must not take the run down with it.
            _logger.LogError(exception, "Failed to write checkpoint to {Path}.", CheckpointPath);
        }
    }
}
