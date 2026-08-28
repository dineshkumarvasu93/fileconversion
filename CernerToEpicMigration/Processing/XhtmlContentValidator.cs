using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>Thrown when a decoded input document holds no XHTML.</summary>
/// <remarks>
/// Derives from <see cref="FormatException"/> so it classifies as a permanent error: a file that
/// holds no markup now will hold none on a retry.
/// </remarks>
public sealed class NotXhtmlContentException : FormatException
{
    public NotXhtmlContentException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The content gate the Telerik importer does not provide.
/// </summary>
/// <remarks>
/// Telerik's HTML importer never rejects anything: an empty file, a stray PDF and a run of binary
/// noise all import and produce a syntactically valid but meaningless RTF, which then looks like
/// a success in every report. Base64 decoding only proves the envelope was intact, not that
/// there is a document inside it. This check is deliberately minimal - it asks whether the
/// payload contains markup at all, not whether the markup is good XHTML - because a Cerner export
/// is a document fragment as often as it is a full <c>&lt;html&gt;</c> document, and rejecting
/// valid fragments would be far worse than passing through odd ones. What it does catch is the
/// three cases that were silently producing empty RTF: an empty payload, a payload with no
/// element tag in it, and binary content.
/// </remarks>
public static partial class XhtmlContentValidator
{
    /// <summary>Characters inspected when deciding whether a payload is binary.</summary>
    private const int BinaryProbeLength = 4096;

    /// <summary>
    /// Share of control characters above which a payload is treated as binary. Real clinical
    /// markup has none; a decoded JPEG or PDF has them throughout.
    /// </summary>
    private const double BinaryControlCharacterRatio = 0.01;

    /// <summary>Checks that a decoded payload is a usable XHTML document.</summary>
    /// <exception cref="NotXhtmlContentException">There is no XHTML in the payload.</exception>
    public static void Validate(string text, string fileName)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new NotXhtmlContentException($"{fileName} decoded to an empty document; there is no XHTML to convert.");

        if (IsBinary(text))
        {
            throw new NotXhtmlContentException(
                $"{fileName} decoded to binary content, not XHTML; it is not a document this stage can convert.");
        }

        if (!ElementPattern().IsMatch(text))
        {
            throw new NotXhtmlContentException(
                $"{fileName} decoded to {text.Length:N0} character(s) with no XHTML element in them; " +
                "the file is not an XHTML document.");
        }
    }

    /// <summary>
    /// True when the head of the payload carries more control characters than markup ever would.
    /// Tab, line feed, carriage return and form feed are ordinary in a document and do not count.
    /// </summary>
    private static bool IsBinary(string text)
    {
        int probeLength = Math.Min(text.Length, BinaryProbeLength);
        int controlCharacters = 0;

        for (int index = 0; index < probeLength; index++)
        {
            char character = text[index];

            if (character is '\t' or '\n' or '\r' or '\f')
                continue;

            if (char.IsControl(character))
                controlCharacters++;
        }

        return controlCharacters > probeLength * BinaryControlCharacterRatio;
    }

    /// <summary>Any opening tag: <c>&lt;html</c>, <c>&lt;p</c>, <c>&lt;ns:body</c>, and so on.</summary>
    [GeneratedRegex("""<\s*[a-zA-Z][a-zA-Z0-9:._-]*(\s|/|>)""")]
    private static partial Regex ElementPattern();
}
