using System.Text;
using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>An input document decoded into text, with the encoding that was used.</summary>
public readonly record struct XhtmlDocument(string Text, Encoding Encoding, long ByteCount);

/// <summary>
/// Reads an XHTML document out of its Base64 envelope, using the encoding the document
/// itself declares.
/// </summary>
/// <remarks>
/// Clinical text is full of accented names, degree signs and micro symbols. Decoding a
/// windows-1252 or ISO-8859-1 export as UTF-8 does not throw - it silently substitutes
/// replacement characters - so the corruption would only surface in Epic. The order of
/// preference is: byte order mark, then the declared encoding of the XML declaration or the
/// HTML meta tag, then UTF-8. The charset is declared inside the payload, so the Base64
/// envelope (see <see cref="Base64InputDecoder"/>) has to come off first.
/// </remarks>
public static partial class XhtmlDocumentReader
{
    /// <summary>Bytes inspected when sniffing for a declared encoding.</summary>
    private const int DeclarationProbeBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads an input document: the Base64 envelope comes off first, then the payload is
    /// decoded with the encoding it declares.
    /// </summary>
    /// <exception cref="Base64DecodingException">The file is not a decodable envelope.</exception>
    public static XhtmlDocument Read(string path) => ReadEnvelope(File.ReadAllBytes(path));

    /// <summary>
    /// Unwraps and decodes an envelope already read from disk.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="Read(string)"/> so a caller that is timing its phases can charge
    /// the disk read and the decode to separate buckets - the two answer different questions when
    /// a run refuses to scale, and one of them is not I/O at all.
    /// </remarks>
    /// <exception cref="Base64DecodingException">The bytes are not a decodable envelope.</exception>
    public static XhtmlDocument ReadEnvelope(byte[] bytes)
    {
        byte[] payload = Base64InputDecoder.Decode(bytes);

        // ByteCount stays the size on disk: the progress figures, the disk-space estimate
        // and the reports all talk about the files the operator can actually see.
        return Decode(payload) with { ByteCount = bytes.Length };
    }

    /// <summary>Decodes the bytes of an XHTML document - the payload, not the envelope.</summary>
    public static XhtmlDocument Decode(byte[] bytes)
    {
        if (TryReadByteOrderMark(bytes, out Encoding? bomEncoding, out int preambleLength))
        {
            return new XhtmlDocument(
                bomEncoding.GetString(bytes, preambleLength, bytes.Length - preambleLength),
                bomEncoding,
                bytes.Length);
        }

        Encoding? declared = ReadDeclaredEncoding(bytes);
        if (declared is not null)
            return new XhtmlDocument(declared.GetString(bytes), declared, bytes.Length);

        // No BOM and nothing declared. Try strict UTF-8 first: if the bytes are not valid
        // UTF-8 the document is almost certainly a single-byte legacy encoding, and
        // windows-1252 round-trips those bytes without loss.
        try
        {
            return new XhtmlDocument(StrictUtf8.GetString(bytes), StrictUtf8, bytes.Length);
        }
        catch (DecoderFallbackException)
        {
            Encoding fallback = GetEncodingOrNull("windows-1252") ?? Encoding.Latin1;
            return new XhtmlDocument(fallback.GetString(bytes), fallback, bytes.Length);
        }
    }

    internal static bool TryReadByteOrderMark(byte[] bytes, out Encoding encoding, out int preambleLength)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
            preambleLength = 4;
            return true;
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
            preambleLength = 4;
            return true;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = Encoding.UTF8;
            preambleLength = 3;
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            preambleLength = 2;
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            preambleLength = 2;
            return true;
        }

        encoding = Encoding.UTF8;
        preambleLength = 0;
        return false;
    }

    private static Encoding? ReadDeclaredEncoding(byte[] bytes)
    {
        // The declaration itself is ASCII, so reading the head as Latin1 is always safe.
        int probeLength = Math.Min(bytes.Length, DeclarationProbeBytes);
        string head = Encoding.Latin1.GetString(bytes, 0, probeLength);

        Match xmlDeclaration = XmlEncodingPattern().Match(head);
        if (xmlDeclaration.Success)
            return GetEncodingOrNull(xmlDeclaration.Groups[1].Value);

        Match metaCharset = MetaCharsetPattern().Match(head);
        return metaCharset.Success ? GetEncodingOrNull(metaCharset.Groups[1].Value) : null;
    }

    private static Encoding? GetEncodingOrNull(string name)
    {
        try
        {
            // UTF-8 is registered without a preamble so a BOM is never re-emitted downstream.
            Encoding encoding = Encoding.GetEncoding(name);
            return encoding.CodePage == Encoding.UTF8.CodePage ? StrictUtf8 : encoding;
        }
        catch (ArgumentException)
        {
            // Unknown or unsupported charset name - fall back to the caller's default.
            return null;
        }
    }

    /// <summary>Registers the legacy single-byte code pages (windows-1252 and friends).</summary>
    public static void RegisterLegacyCodePages() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [GeneratedRegex("""<\?xml[^>]*?encoding\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex XmlEncodingPattern();

    [GeneratedRegex("""<meta[^>]*?charset\s*=\s*["']?\s*([\w-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetPattern();
}
