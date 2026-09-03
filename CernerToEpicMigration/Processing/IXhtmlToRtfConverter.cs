using System.Diagnostics;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Where the wall-clock time of one document went, in <see cref="Stopwatch"/> ticks.
/// </summary>
/// <remarks>
/// This is the answer to "why does adding threads not add throughput?". The per-worker service
/// time says <em>that</em> a document costs more when workers are added; this says <em>which
/// part</em> of it does, and the parts fall into two groups that call for opposite fixes:
/// <list type="bullet">
/// <item><description>
/// <see cref="ImportTicks"/> and <see cref="ExportTicks"/> are Telerik, in-process and CPU-bound.
/// If these are what grows with the thread count, the machine is out of cores or the library is
/// serialising internally - and no amount of disk tuning helps.
/// </description></item>
/// <item><description>
/// <see cref="ReadTicks"/>, <see cref="WriteTicks"/> and <see cref="ArchiveTicks"/> are the file
/// system. If these are what grows, the workers are queueing on the volume, on the NTFS directory
/// index or on an on-access virus scanner - and adding cores helps nothing.
/// </description></item>
/// </list>
/// Ticks rather than milliseconds so a per-document figure can be summed across a million
/// documents without rounding drift. Every field is a duration; nothing overlaps, so the sum is
/// the time the worker held the document minus retry backoff and bookkeeping.
/// </remarks>
public readonly record struct ConversionPhases(
    long ReadTicks,
    long DecodeTicks,
    long ValidateTicks,
    long ImportTicks,
    long ExportTicks,
    long WriteTicks,
    long ArchiveTicks = 0)
{
    /// <summary>The same breakdown with the archive move - measured by the caller - filled in.</summary>
    public ConversionPhases WithArchive(long archiveTicks) => this with { ArchiveTicks = archiveTicks };

    /// <summary>Total measured time, which is everything but the unattributed remainder.</summary>
    public long TotalTicks =>
        ReadTicks + DecodeTicks + ValidateTicks + ImportTicks + ExportTicks + WriteTicks + ArchiveTicks;
}

/// <summary>Bytes read from the XHTML input and written to the RTF output, and where the time went.</summary>
public readonly record struct ConversionOutcome(long InputBytes, long OutputBytes, ConversionPhases Phases = default);

/// <summary>
/// Stage 1 conversion primitive: one XHTML document in, one RTF document out.
/// Implementations must be safe to call concurrently from worker threads.
/// </summary>
public interface IXhtmlToRtfConverter
{
    ConversionOutcome Convert(string inputPath, string outputPath);
}
