using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>Removes namespace declarations from the opening <c>html</c> element in XHTML.</summary>
internal static partial class XhtmlRootNamespaceRemover
{
    #region Public Methods

    /// <summary>Removes default and prefixed namespace declarations from the opening html tag.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML with root html namespace declarations removed.</returns>
    public static string Remove(string xhtml)
    {
        Match htmlTag = HtmlOpeningTagPattern().Match(xhtml);
        if (!htmlTag.Success)
            return xhtml;

        string cleanedTag = NamespaceAttributePattern().Replace(htmlTag.Value, string.Empty);
        if (cleanedTag == htmlTag.Value)
            return xhtml;

        return string.Concat(
            xhtml.AsSpan(0, htmlTag.Index),
            cleanedTag,
            xhtml.AsSpan(htmlTag.Index + htmlTag.Length));
    }

    #endregion

    #region Private Methods

    /// <summary>Creates a match for the opening html tag, including its attributes.</summary>
    /// <returns>A regex that matches the first opening html tag.</returns>
    [GeneratedRegex("""<html\b(?:"[^"]*"|'[^']*'|[^'">])*>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlOpeningTagPattern();

    /// <summary>Creates a match for default and prefixed XML namespace attributes.</summary>
    /// <returns>A regex that matches <c>xmlns</c> and <c>xmlns:prefix</c> attributes.</returns>
    [GeneratedRegex("""\s+xmlns(?::[A-Za-z_][A-Za-z0-9_.-]*)?\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceAttributePattern();

    #endregion
}
