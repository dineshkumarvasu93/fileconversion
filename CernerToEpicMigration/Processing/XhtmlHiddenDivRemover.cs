using System.Text;
using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>Removes configured-style <c>div</c> and <c>span</c> elements from XHTML.</summary>
internal static partial class XhtmlConfiguredStyleElementRemover
{
    #region Public Methods

    /// <summary>Removes complete target elements whose inline style contains the configured match text.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <param name="styleMatchText">The inline-style text used to identify elements for removal.</param>
    /// <returns>The XHTML with matching elements removed.</returns>
    public static string Remove(string xhtml, string styleMatchText)
    {
        MatchCollection tags = TagPattern().Matches(xhtml);
        StringBuilder result = new(xhtml.Length);
        int copiedThrough = 0;
        int index = 0;

        while (index < tags.Count)
        {
            Match opening = tags[index];
            if (!IsTargetElement(opening) || opening.Groups["closing"].Success ||
                !HasConfiguredStyle(opening.Groups["attributes"].Value, styleMatchText))
            {
                index++;
                continue;
            }

            if (opening.Groups["attributes"].Value.TrimEnd().EndsWith('/'))
            {
                result.Append(xhtml, copiedThrough, opening.Index - copiedThrough);
                copiedThrough = opening.Index + opening.Length;
                index++;
                continue;
            }

            int depth = 1;
            int closingIndex = -1;
            for (int nestedIndex = index + 1; nestedIndex < tags.Count; nestedIndex++)
            {
                Match nested = tags[nestedIndex];
                if (!nested.Groups["name"].Value.Equals(
                    opening.Groups["name"].Value,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isClosing = nested.Groups["closing"].Success;
                if (isClosing)
                {
                    depth--;
                    if (depth == 0)
                    {
                        closingIndex = nestedIndex;
                        break;
                    }
                }
                else if (!nested.Groups["attributes"].Value.TrimEnd().EndsWith('/'))
                {
                    depth++;
                }
            }

            if (closingIndex < 0)
            {
                index++;
                continue;
            }

            Match closing = tags[closingIndex];
            result.Append(xhtml, copiedThrough, opening.Index - copiedThrough);
            copiedThrough = closing.Index + closing.Length;
            index = closingIndex + 1;
        }

        if (copiedThrough == 0)
            return xhtml;

        result.Append(xhtml, copiedThrough, xhtml.Length - copiedThrough);
        return result.ToString();
    }

    #endregion

    #region Private Methods

    /// <summary>Determines whether the tag is a supported target element.</summary>
    /// <param name="tag">The parsed XHTML tag.</param>
    /// <returns><see langword="true"/> for a <c>div</c> or <c>span</c> tag.</returns>
    private static bool IsTargetElement(Match tag) =>
        tag.Groups["name"].Value.Equals("div", StringComparison.OrdinalIgnoreCase) ||
        tag.Groups["name"].Value.Equals("span", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks whether a tag's inline style contains the configured match text.</summary>
    /// <param name="attributes">The tag's attribute text.</param>
    /// <param name="styleMatchText">The configured style text.</param>
    /// <returns><see langword="true"/> when the inline style matches.</returns>
    private static bool HasConfiguredStyle(string attributes, string styleMatchText)
    {
        if (string.IsNullOrWhiteSpace(styleMatchText))
            return false;

        Match styleAttribute = StyleAttributePattern().Match(attributes);
        if (!styleAttribute.Success)
            return false;

        string style = styleAttribute.Groups["double"].Value;
        if (styleAttribute.Groups["single"].Success)
            style = styleAttribute.Groups["single"].Value;
        else if (styleAttribute.Groups["unquoted"].Success)
            style = styleAttribute.Groups["unquoted"].Value;

        string normalizedStyle = NormalizeStyleText(style);
        string normalizedMatchText = NormalizeStyleText(styleMatchText);
        return normalizedStyle.Contains(normalizedMatchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes whitespace so style matching is insensitive to CSS spacing.</summary>
    /// <param name="value">The style text to normalize.</param>
    /// <returns>The style text without whitespace.</returns>
    private static string NormalizeStyleText(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    /// <summary>Creates matches for markup tokens while skipping comments and declarations.</summary>
    /// <returns>A regex that matches XHTML tags and non-element markup tokens.</returns>
    [GeneratedRegex("""<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?(?:[^?]|\?(?!>))*\?>|<![^>]*>|<(?<closing>/)?(?<name>[A-Za-z][A-Za-z0-9:._-]*)\b(?<attributes>(?:"[^"]*"|'[^']*'|[^'">])*)>""")]
    private static partial Regex TagPattern();

    /// <summary>Creates matches for an inline <c>style</c> attribute.</summary>
    /// <returns>A regex that captures quoted or unquoted style values.</returns>
    [GeneratedRegex("""(?:^|\s)style\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<unquoted>[^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleAttributePattern();

    #endregion
}
