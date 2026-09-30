using System.Text;
using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Rewrites standard <c>ul</c>/<c>li</c> lists to explicit bullet paragraphs with
/// hanging indents.
/// </summary>
/// <remarks>
/// Telerik imports a <c>ul</c> as a real list and exports it with an RTF list
/// definition whose bullet maps to a Symbol-font private-use character (U+F0B7)
/// that Word may not display. An explicit U+2022 bullet paragraph always renders.
/// The rewrite is kept deliberately narrow - only a leaf <c>ul</c> whose content is
/// nothing but whitespace and direct <c>li</c> children is touched - so a list that
/// carries stray text, other markup or an unrecognized marker passes through
/// byte-identical. Nested lists convert innermost first across passes.
/// </remarks>
internal static partial class XhtmlUnorderedListToBulletParagraphConverter
{
    private const int MaxPasses = 32;

    #region Public Methods

    /// <summary>Converts qualifying unordered lists to explicit bullet paragraphs.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML with lists replaced by bullet paragraphs, or the input unchanged.</returns>
    public static string Convert(string xhtml)
    {
        string current = xhtml;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            string next = ConvertPass(current);
            if (ReferenceEquals(next, current))
                return current;

            current = next;
        }

        return current;
    }

    #endregion

    #region Private Methods

    /// <summary>Converts every qualifying leaf <c>ul</c> in one pass.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML after one pass, or the input unchanged.</returns>
    private static string ConvertPass(string xhtml)
    {
        MatchCollection tags = XhtmlMarkup.TagPattern().Matches(xhtml);
        List<(int Start, int End, string Replacement)> rewrites = new();

        for (int index = 0; index < tags.Count; index++)
        {
            Match opening = tags[index];
            if (!XhtmlMarkup.IsOpeningTag(opening, "ul"))
                continue;

            int closingIndex = XhtmlMarkup.FindMatchingClose(tags, index);
            if (closingIndex < 0)
                continue;

            Match closing = tags[closingIndex];
            if (TryBuildParagraphs(xhtml, tags, index, closingIndex, out string? paragraphs))
            {
                rewrites.Add((opening.Index, closing.Index + closing.Length, paragraphs!));
                index = closingIndex;
            }
        }

        if (rewrites.Count == 0)
            return xhtml;

        StringBuilder result = new(xhtml.Length);
        int copiedThrough = 0;
        foreach ((int start, int end, string replacement) in rewrites)
        {
            result.Append(xhtml, copiedThrough, start - copiedThrough);
            result.Append(replacement);
            copiedThrough = end;
        }

        result.Append(xhtml, copiedThrough, xhtml.Length - copiedThrough);
        return result.ToString();
    }

    /// <summary>Builds the replacement paragraphs when the list qualifies; leaves it otherwise.</summary>
    /// <param name="xhtml">The XHTML being processed.</param>
    /// <param name="tags">The pre-matched markup tokens.</param>
    /// <param name="openIndex">The index of the <c>ul</c>'s opening tag.</param>
    /// <param name="closeIndex">The index of the <c>ul</c>'s closing tag.</param>
    /// <param name="paragraphs">The replacement markup when the list qualifies.</param>
    /// <returns><see langword="true"/> when the list is a plain item list.</returns>
    private static bool TryBuildParagraphs(
        string xhtml,
        MatchCollection tags,
        int openIndex,
        int closeIndex,
        out string? paragraphs)
    {
        paragraphs = null;

        // Leaf lists only: a nested list inside an item is left for a later pass,
        // which is what makes the conversion innermost-first.
        for (int index = openIndex + 1; index < closeIndex; index++)
        {
            Group name = tags[index].Groups["name"];
            if (name.Success &&
                (name.Value.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
                 name.Value.Equals("ol", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        List<(Match Open, Match Close)> items = new();
        int gapStart = tags[openIndex].Index + tags[openIndex].Length;
        for (int index = openIndex + 1; index < closeIndex; index++)
        {
            Match tag = tags[index];

            if (!xhtml.AsSpan(gapStart, tag.Index - gapStart).IsWhiteSpace())
                return false;

            // Anything at the top level that is not a complete <li>...</li> -
            // stray markup, a closing tag, a self-closing <li/> - disqualifies the list.
            if (!XhtmlMarkup.IsOpeningTag(tag, "li"))
                return false;

            int liClose = XhtmlMarkup.FindMatchingClose(tags, index);
            if (liClose < 0 || liClose >= closeIndex)
                return false;

            items.Add((tag, tags[liClose]));
            gapStart = tags[liClose].Index + tags[liClose].Length;
            index = liClose;
        }

        if (items.Count == 0)
            return false;

        if (!xhtml.AsSpan(gapStart, tags[closeIndex].Index - gapStart).IsWhiteSpace())
            return false;

        string? marker = ResolveMarker(tags[openIndex].Groups["attributes"].Value);
        if (marker is null)
            return false;

        StringBuilder markup = new();
        foreach ((Match open, Match close) in items)
        {
            string inner = xhtml
                .Substring(open.Index + open.Length, close.Index - open.Index - open.Length)
                .Trim();
            markup.Append("<div style=\"margin-left: 32px; text-indent: -17px;\">");
            markup.Append(marker);
            markup.Append("&#160;");
            markup.Append(inner);
            markup.Append("</div>");
        }

        paragraphs = markup.ToString();
        return true;
    }

    /// <summary>Resolves the bullet entity for the list's <c>list-style</c> marker.</summary>
    /// <param name="attributes">The <c>ul</c>'s attribute text.</param>
    /// <returns>
    /// The bullet entity for <c>disc</c> (the default), <c>circle</c> or <c>square</c>;
    /// <see langword="null"/> for <c>none</c> or an unrecognized marker, which leaves
    /// the list unchanged.
    /// </returns>
    private static string? ResolveMarker(string attributes)
    {
        Match style = XhtmlMarkup.StyleAttributePattern().Match(attributes);
        if (!style.Success)
            return "&#8226;";

        string styleValue = style.Groups["double"].Success ? style.Groups["double"].Value
            : style.Groups["single"].Success ? style.Groups["single"].Value
            : style.Groups["unquoted"].Value;

        Match declaration = ListStyleTypePattern().Match(styleValue);
        if (!declaration.Success)
            declaration = ListStylePattern().Match(styleValue);
        if (!declaration.Success)
            return "&#8226;";

        Match keyword = MarkerKeywordPattern().Match(declaration.Groups["value"].Value);
        if (!keyword.Success)
            return null;

        return keyword.Value.ToLowerInvariant() switch
        {
            "disc" => "&#8226;",
            "circle" => "&#9702;",
            "square" => "&#9642;",
            _ => null
        };
    }

    /// <summary>Creates matches for a <c>list-style-type</c> declaration.</summary>
    /// <returns>A regex that captures the declaration's value.</returns>
    [GeneratedRegex("""(?:^|;)\s*list-style-type\s*:\s*(?<value>[^;]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListStyleTypePattern();

    /// <summary>Creates matches for a <c>list-style</c> shorthand declaration.</summary>
    /// <returns>A regex that captures the declaration's value.</returns>
    [GeneratedRegex("""(?:^|;)\s*list-style\s*:\s*(?<value>[^;]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListStylePattern();

    /// <summary>Creates matches for a list marker keyword.</summary>
    /// <returns>A regex that captures <c>disc</c>, <c>circle</c>, <c>square</c> or <c>none</c>.</returns>
    [GeneratedRegex("""\b(disc|circle|square|none)\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerKeywordPattern();

    #endregion
}
