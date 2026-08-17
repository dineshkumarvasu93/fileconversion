using System.Text;

namespace CernerToEpicMigration.Processing;

/// <summary>Thrown when an input document is not a decodable Base64 envelope.</summary>
/// <remarks>
/// Derives from <see cref="FormatException"/> so that a decoding failure is classified
/// as a permanent error: a file that is not Base64 now will not be Base64 on a retry.
/// </remarks>
public sealed class Base64DecodingException : FormatException
{
    public Base64DecodingException(string message)
        : base(message)
    {
    }

    public Base64DecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Unwraps the Base64 envelope that each input document arrives in.
/// </summary>
/// <remarks>
/// The bytes on disk are the envelope, not the markup, so this runs before any encoding
/// detection - the XHTML declares its charset inside the payload. Base64 is ASCII by
/// definition, so a file saved with a byte order mark and the line breaks encoders insert
/// every 64 or 76 columns are both tolerated, while a byte above 0x7F is corruption and
/// fails. Failures carry their own exception type so a broken envelope is never mistaken
/// for a broken document: the file is moved to the error folder with the reason recorded.
/// The message never quotes the file content, which is clinical data.
/// </remarks>
public static class Base64InputDecoder
{
    /// <summary>Decodes the envelope read from disk into the bytes of the XHTML document.</summary>
    /// <exception cref="Base64DecodingException">The content is not usable Base64.</exception>
    public static byte[] Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
            throw new Base64DecodingException("The file is empty: there is no Base64 content to decode.");

        byte[] decoded;

        try
        {
            // FromBase64String ignores tabs, spaces and line breaks, which is what the
            // column-wrapped output of a Base64 encoder is padded with.
            decoded = Convert.FromBase64String(ReadEnvelopeText(bytes));
        }
        catch (FormatException exception)
        {
            throw new Base64DecodingException(
                $"The file is not valid Base64 content: {exception.Message}", exception);
        }

        if (decoded.Length == 0)
            throw new Base64DecodingException("The Base64 content decoded to an empty document.");

        return decoded;
    }

    /// <summary>Reads the envelope as text, honouring a byte order mark if one was written.</summary>
    private static string ReadEnvelopeText(byte[] bytes)
    {
        if (XhtmlDocumentReader.TryReadByteOrderMark(bytes, out Encoding encoding, out int preambleLength))
            return encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

        // ASCII maps every byte above 0x7F to '?', which is not a Base64 character, so a
        // corrupted envelope surfaces as a decoding failure rather than decoding anyway.
        return Encoding.ASCII.GetString(bytes);
    }
}
