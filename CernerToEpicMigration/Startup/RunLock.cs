using System.Globalization;
using System.Text;

namespace CernerToEpicMigration.Startup;

/// <summary>
/// A single-instance guard per report folder. Two runs over the same input would race for
/// the same files and log each other's documents as failures, so the second one is refused.
/// </summary>
public sealed class RunLock : IDisposable
{
    public const string LockFileName = "migration.lock";

    private readonly FileStream _stream;

    private RunLock(FileStream stream)
    {
        _stream = stream;
    }

    public string Path { get; private init; } = string.Empty;

    /// <summary>
    /// Takes the lock, or returns null with the reason when another run holds it.
    /// The lock is released when the process exits, even if it is killed.
    /// </summary>
    public static RunLock? TryAcquire(string reportBasePath, out string? reason)
    {
        string fullPath = System.IO.Path.GetFullPath(reportBasePath);
        string lockPath = System.IO.Path.Combine(fullPath, LockFileName);

        try
        {
            Directory.CreateDirectory(fullPath);

            FileStream stream = new(
                lockPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 512,
                FileOptions.DeleteOnClose);

            byte[] details = Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"machine={Environment.MachineName} pid={Environment.ProcessId} started={DateTimeOffset.UtcNow:u}"));

            stream.Write(details);
            stream.Flush();

            reason = null;
            return new RunLock(stream) { Path = lockPath };
        }
        catch (IOException)
        {
            reason =
                $"Another migration run is already using {fullPath} (lock file {LockFileName}). " +
                "Wait for it to finish, or point --output/ReportBasePath at a different folder for this run.";
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            reason = $"The run lock could not be created in {fullPath}: {exception.Message}";
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
