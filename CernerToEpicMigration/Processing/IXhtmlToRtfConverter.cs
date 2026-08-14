namespace CernerToEpicMigration.Processing;

/// <summary>Bytes read from the XHTML input and written to the RTF output.</summary>
public readonly record struct ConversionOutcome(long InputBytes, long OutputBytes);

/// <summary>
/// Stage 1 conversion primitive: one XHTML document in, one RTF document out.
/// Implementations must be safe to call concurrently from worker threads.
/// </summary>
public interface IXhtmlToRtfConverter
{
    ConversionOutcome Convert(string inputPath, string outputPath);
}
