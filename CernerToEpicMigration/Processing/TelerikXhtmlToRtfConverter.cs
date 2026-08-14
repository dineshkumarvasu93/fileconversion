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
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"XHTML file not found: {inputPath}", inputPath);

        if (!_options.OverwriteExistingRtf && File.Exists(outputPath))
            throw new IOException($"RTF output already exists and overwrite is disabled: {outputPath}");

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        TimeSpan timeout = _options.ConversionTimeout;

        // Decode with the encoding the document declares, not an assumed one - see
        // XhtmlDocumentReader for why that matters to clinical text.
        XhtmlDocument input = XhtmlDocumentReader.Read(inputPath);

        HtmlFormatProvider htmlProvider = new();
        RadFlowDocument document = htmlProvider.Import(input.Text, timeout);

        RtfFormatProvider rtfProvider = new();
        string rtf = rtfProvider.Export(document, timeout);

        // Write to a temporary file first so an interrupted run never leaves a
        // half-written RTF that a later stage would treat as valid.
        string tempPath = outputPath + ".tmp";
        byte[] rtfBytes = RtfEncoding.GetBytes(rtf);
        File.WriteAllBytes(tempPath, rtfBytes);
        File.Move(tempPath, outputPath, overwrite: true);

        return new ConversionOutcome(InputBytes: input.ByteCount, OutputBytes: rtfBytes.Length);
    }
}
