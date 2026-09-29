using System.Text;
using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>Removes empty, link-less <c>a</c> elements from XHTML.</summary>
/// <remarks>
/// Cerner places an empty named anchor - <c>&lt;a name="_guid"&gt;&lt;/a&gt;</c> - before
/// almost every section and line as an in-page jump target. Telerik's importer turns every
/// <c>a</c> into a <c>HYPERLINK</c> field, so an anchor without an address becomes
/// <c>HYPERLINK ""</c>, which Word renders as "Error! Hyperlink reference not valid." once
/// per anchor. Only anchors with no <c>href</c> and no content are removed: they carry no
/// visible text and nothing links to them, while an anchor with an address or text passes
/// through untouched.
/// </remarks>
internal static partial class XhtmlEmptyAnchorRemover
{
    #region Public Methods

    /// <summary>Removes <c>a</c> elements that have no <c>href</c> and no content.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML with empty anchors removed, or the input unchanged.</returns>
    public static string Remove(string xhtml)
    {
        MatchCollection tags = XhtmlMarkup.TagPattern().Matches(xhtml);
        StringBuilder result = new(xhtml.Length);
        int copiedThrough = 0;

        for (int index = 0; index < tags.Count; index++)
        {
            Match opening = tags[index];
            if (!IsAnchor(opening) || opening.Groups["closing"].Success ||
                HrefAttributePattern().IsMatch(opening.Groups["attributes"].Value))
            {
                continue;
            }

            int end;
            if (opening.Groups["attributes"].Value.TrimEnd().EndsWith('/'))
            {
                end = opening.Index + opening.Length;
            }
            else
            {
                if (index + 1 >= tags.Count)
                    continue;

                Match closing = tags[index + 1];
                int contentStart = opening.Index + opening.Length;
                if (!IsAnchor(closing) || !closing.Groups["closing"].Success ||
                    !xhtml.AsSpan(contentStart, closing.Index - contentStart).IsWhiteSpace())
                {
                    continue;
                }

                end = closing.Index + closing.Length;
                index++;
            }

            result.Append(xhtml, copiedThrough, opening.Index - copiedThrough);
            copiedThrough = end;
        }

        if (copiedThrough == 0)
            return xhtml;

        result.Append(xhtml, copiedThrough, xhtml.Length - copiedThrough);
        return result.ToString();
    }

    #endregion

    #region Private Methods

    /// <summary>Determines whether the tag is an <c>a</c> element.</summary>
    /// <param name="tag">The parsed XHTML tag.</param>
    /// <returns><see langword="true"/> for an opening or closing <c>a</c> tag.</returns>
    private static bool IsAnchor(Match tag) =>
        tag.Groups["name"].Success &&
        tag.Groups["name"].Value.Equals("a", StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a match for an <c>href</c> attribute.</summary>
    /// <returns>A regex that matches an <c>href</c> attribute but not <c>data-href</c>.</returns>
    [GeneratedRegex("""(?:^|\s)href\s*=""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefAttributePattern();

    #endregion
}
