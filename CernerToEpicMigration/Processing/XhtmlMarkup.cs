using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>Markup patterns shared by the XHTML pre-processing passes.</summary>
internal static partial class XhtmlMarkup
{
    #region Internal Methods

    /// <summary>Creates matches for markup tokens while skipping comments and declarations.</summary>
    /// <returns>A regex that matches XHTML tags and non-element markup tokens.</returns>
    [GeneratedRegex("""<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?(?:[^?]|\?(?!>))*\?>|<![^>]*>|<(?<closing>/)?(?<name>[A-Za-z][A-Za-z0-9:._-]*)\b(?<attributes>(?:"[^"]*"|'[^']*'|[^'">])*)>""")]
    internal static partial Regex TagPattern();

    /// <summary>Creates matches for an inline <c>style</c> attribute.</summary>
    /// <returns>A regex that captures quoted or unquoted style values.</returns>
    [GeneratedRegex("""(?:^|\s)style\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<unquoted>[^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex StyleAttributePattern();

    /// <summary>Finds the closing tag matching an opening tag at <paramref name="openIndex"/>.</summary>
    /// <param name="tags">The pre-matched markup tokens.</param>
    /// <param name="openIndex">The index of the opening tag.</param>
    /// <returns>The index of the matching closing tag, or -1 for unbalanced markup.</returns>
    internal static int FindMatchingClose(MatchCollection tags, int openIndex)
    {
        string name = tags[openIndex].Groups["name"].Value;
        int depth = 1;
        for (int index = openIndex + 1; index < tags.Count; index++)
        {
            Match nested = tags[index];
            if (!nested.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (nested.Groups["closing"].Success)
            {
                if (--depth == 0)
                    return index;
            }
            else if (!nested.Groups["attributes"].Value.TrimEnd().EndsWith('/'))
            {
                depth++;
            }
        }

        return -1;
    }

    /// <summary>Determines whether a match is an opening tag of the given element.</summary>
    /// <param name="tag">The parsed XHTML tag.</param>
    /// <param name="name">The element name to compare.</param>
    /// <returns><see langword="true"/> for a non-closing, non-self-closing tag of that name.</returns>
    internal static bool IsOpeningTag(Match tag, string name) =>
        tag.Groups["name"].Success &&
        !tag.Groups["closing"].Success &&
        tag.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase) &&
        !tag.Groups["attributes"].Value.TrimEnd().EndsWith('/');

    #endregion
}
