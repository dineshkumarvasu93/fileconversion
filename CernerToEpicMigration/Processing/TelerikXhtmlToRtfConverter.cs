using System.Diagnostics;
using System.Text;
using CernerToEpicMigration.Configuration;
using Telerik.Windows.Documents.Flow.FormatProviders.Html;
using Telerik.Windows.Documents.Flow.FormatProviders.Rtf;
using Telerik.Windows.Documents.Flow.Model;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Design document section 8.1 - read XHTML, import with <see cref="HtmlFormatProvider"/>,
/// export with <see cref="RtfFormatProvider"/>, write the RTF file.
/// </summary>
/// <remarks>
/// The format providers carry per-import state, so a fresh instance is created per
/// document instead of sharing one across worker threads.
/// The RTF is written without a byte order mark: an RTF reader expects the stream to
/// begin with <c>{\rtf1</c>, and a leading BOM trips up some importers.
/// </remarks>
public sealed class TelerikXhtmlToRtfConverter : IXhtmlToRtfConverter
{
    private static readonly UTF8Encoding RtfEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ProcessingOptions _options;

    public TelerikXhtmlToRtfConverter(ProcessingOptions options)
    {
        _options = options;
    }

    public ConversionOutcome Convert(string inputPath, string outputPath)
    {
        TimeSpan timeout = _options.ConversionTimeout;

        // Each phase is timed on the way through. The cost is two Stopwatch reads per phase -
        // a handful of nanoseconds against a document that takes milliseconds - and it is what
        // turns "more threads did not help" into a specific answer; see ConversionPhases.
        // The mark starts before the existence checks so that every syscall this method makes is
        // charged to a phase: on a contended volume the metadata probes are not free, and a
        // breakdown that quietly omits them would point at the wrong bottleneck.
        long mark = Stopwatch.GetTimestamp();

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"XHTML file not found: {inputPath}", inputPath);

        if (!_options.OverwriteExistingRtf && File.Exists(outputPath))
            throw new IOException($"RTF output already exists and overwrite is disabled: {outputPath}");

        byte[] envelope = File.ReadAllBytes(inputPath);
        long readTicks = Lap(ref mark);

        // Decode with the encoding the document declares, not an assumed one - see
        // XhtmlDocumentReader for why that matters to clinical text.
        XhtmlDocument input = XhtmlDocumentReader.ReadEnvelope(envelope);
        long decodeTicks = Lap(ref mark);

        // The importer below accepts anything, including an empty payload and binary noise, and
        // exports a valid but meaningless RTF for it - which then counts as a converted document
        // everywhere downstream. This is the only place that can say no.
        if (_options.ValidateXhtmlContent)
            XhtmlContentValidator.Validate(input.Text, Path.GetFileName(inputPath));

        long validateTicks = Lap(ref mark);

        HtmlFormatProvider htmlProvider = new();
        RadFlowDocument document = htmlProvider.Import(input.Text, timeout);
        long importTicks = Lap(ref mark);

        RtfFormatProvider rtfProvider = new();
        string rtf = rtfProvider.Export(document, timeout);
        long exportTicks = Lap(ref mark);

        // Created here rather than at the top of the method so the directory probe is charged to
        // the write phase along with the writes it exists for.
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        // Write to a temporary file first so an interrupted run never leaves a
        // half-written RTF that a later stage would treat as valid.
        string tempPath = outputPath + ".tmp";
        byte[] outputBytes = EncodeOutput(rtf);
        File.WriteAllBytes(tempPath, outputBytes);
        File.Move(tempPath, outputPath, overwrite: true);
        long writeTicks = Lap(ref mark);

        return new ConversionOutcome(
            InputBytes: input.ByteCount,
            OutputBytes: outputBytes.Length,
            Phases: new ConversionPhases(
                readTicks, decodeTicks, validateTicks, importTicks, exportTicks, writeTicks));
    }

    /// <summary>Ticks since the last mark, moving the mark to now.</summary>
    private static long Lap(ref long mark)
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - mark;
        mark = now;
        return elapsed;
    }

    /// <summary>
    /// The bytes that go into the output file: the RTF itself, or - when
    /// <see cref="ProcessingOptions.EncodeRtfOutputAsBase64"/> is on - a Base64 envelope
    /// around it, the same shape the input documents arrive in. The envelope is written on
    /// one line and in ASCII, so it decodes with a plain <c>Convert.FromBase64String</c>.
    /// </summary>
    private byte[] EncodeOutput(string rtf)
    {
        byte[] rtfBytes = RtfEncoding.GetBytes(rtf);

        if (!_options.EncodeRtfOutputAsBase64)
            return rtfBytes;

        // System.Convert, not this class's own Convert method.
        return Encoding.ASCII.GetBytes(System.Convert.ToBase64String(rtfBytes));
    }
}
