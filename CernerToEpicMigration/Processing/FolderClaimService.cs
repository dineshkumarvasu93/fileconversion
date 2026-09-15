using System.Globalization;
using System.Text;
using CernerToEpicMigration.Configuration;
using CernerToEpicMigration.Models;
using Microsoft.Extensions.Logging;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Hands one input folder at a time to one instance, so several instances can run over the same
/// <c>InputBasePath</c> without two of them converting the same document.
/// </summary>
/// <remarks>
/// The folders are still discovered dynamically by <see cref="FileDiscoveryService"/>; nothing
/// here knows or cares what they are called or how many there are. Each instance walks the same
/// discovered list and takes the folders nobody else has, which distributes the work by whoever
/// is free rather than by a fixed assignment - an instance that draws a small folder comes back
/// for another one while a slower instance is still on its first.
/// <para>
/// Coordination is two files per folder in <see cref="MigrationConfig.ClaimFolderPath"/>:
/// </para>
/// <list type="bullet">
/// <item><c>{folder}.claim</c> - the lease, held open for as long as the folder is being
/// processed. It is opened <see cref="FileOptions.DeleteOnClose"/>, so Windows drops it when the
/// process exits even if the instance is killed mid-folder, and the folder becomes available
/// again rather than being stranded for the rest of the run.</item>
/// <item><c>{folder}.done</c> - written after the folder completes and never removed. Without it
/// a finished folder would be re-claimable the moment its lease was released, and a second
/// instance still walking the list would pick it up again; with it, a folder is processed exactly
/// once per run.</item>
/// </list>
/// <para>
/// The mutual exclusion is the file system's, not ours: the second instance to open the same
/// lease path for writing gets an <see cref="IOException"/> from the OS. That is the same
/// mechanism <see cref="Startup.RunLock"/> uses, and it holds across processes and across
/// machines sharing the folder over SMB, which a lock inside one process would not.
/// </para>
/// </remarks>
public sealed class FolderClaimService
{
    private const string ClaimExtension = ".claim";
    private const string CompletedExtension = ".done";

    private readonly MigrationConfig _config;
    private readonly ILogger<FolderClaimService> _logger;

    public FolderClaimService(MigrationConfig config, ILogger<FolderClaimService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// True when this run coordinates with other instances. Claiming is opt-in: without an
    /// instance id a single run behaves exactly as it always has, and writes no coordination
    /// files into the input share.
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_config.InstanceId);

    /// <summary>The folder holding the lease and completion files.</summary>
    public string ClaimFolderPath => Path.GetFullPath(_config.ClaimFolderPath);

    /// <summary>
    /// Tries to take the folder for this instance. Returns null when another instance holds it
    /// or has already finished it, in which case the caller moves on to the next folder.
    /// </summary>
    public FolderClaim? TryClaim(DateFolder folder)
    {
        if (!Enabled)
            return FolderClaim.Unmanaged(folder);

        string claimFolder = ClaimFolderPath;

        try
        {
            Directory.CreateDirectory(claimFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a coordination folder there is no safe way to divide the work, and
            // processing everything would be the duplicate run claiming exists to prevent.
            throw new InvalidOperationException(
                $"The claim folder {claimFolder} could not be created: {exception.Message}", exception);
        }

        string completedPath = Path.Combine(claimFolder, folder.Name + CompletedExtension);
        if (File.Exists(completedPath))
        {
            _logger.LogInformation(
                "Skipping folder {Folder}: already completed by another instance.", folder.Name);
            return null;
        }

        string claimPath = Path.Combine(claimFolder, folder.Name + ClaimExtension);

        FileStream lease;
        try
        {
            lease = new FileStream(
                claimPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 512,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            _logger.LogInformation("Skipping folder {Folder}: claimed by another instance.", folder.Name);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                exception, "Skipping folder {Folder}: the claim file could not be written.", folder.Name);
            return null;
        }

        WriteDetails(lease);

        _logger.LogInformation("Claimed folder {Folder}.", folder.Name);

        return new FolderClaim(folder, lease, completedPath, _config.InstanceId, _logger);
    }

    private void WriteDetails(FileStream lease)
    {
        byte[] details = Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"instance={_config.InstanceId} machine={Environment.MachineName} pid={Environment.ProcessId} claimed={DateTimeOffset.UtcNow:u}"));

        lease.Write(details);
        lease.Flush();
    }
}

/// <summary>
/// One instance's hold on one input folder. Disposing releases the lease; calling
/// <see cref="MarkCompleted"/> first also records the folder as finished for the rest of the run.
/// </summary>
public sealed class FolderClaim : IDisposable
{
    private readonly FileStream? _lease;
    private readonly string? _completedPath;
    private readonly string? _instanceId;
    private readonly ILogger? _logger;

    internal FolderClaim(
        DateFolder folder, FileStream lease, string completedPath, string instanceId, ILogger logger)
    {
        Folder = folder;
        _lease = lease;
        _completedPath = completedPath;
        _instanceId = instanceId;
        _logger = logger;
    }

    private FolderClaim(DateFolder folder)
    {
        Folder = folder;
    }

    /// <summary>A claim that owns nothing, for a single-instance run with coordination off.</summary>
    internal static FolderClaim Unmanaged(DateFolder folder) => new(folder);

    public DateFolder Folder { get; }

    /// <summary>
    /// Records the folder as finished so no other instance picks it up. Left uncalled - because
    /// the folder was cancelled or the run stopped early - the folder stays available, which is
    /// what a resumed or restarted instance needs.
    /// </summary>
    public void MarkCompleted()
    {
        if (_completedPath is null)
            return;

        try
        {
            File.WriteAllText(_completedPath, string.Create(
                CultureInfo.InvariantCulture,
                $"instance={_instanceId} machine={Environment.MachineName} pid={Environment.ProcessId} completed={DateTimeOffset.UtcNow:u}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder is converted either way; the only cost is that another instance may
            // claim it and find an empty folder, so this is a warning and not a failure.
            _logger?.LogWarning(
                exception, "Folder {Folder} finished but could not be marked complete.", Folder.Name);
        }
    }

    public void Dispose() => _lease?.Dispose();
}
