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

    #endregion
}
