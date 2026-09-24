using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>Removes complete script elements from XHTML.</summary>
internal static partial class XhtmlScriptRemover
{
    #region Public Methods

    /// <summary>Removes all script elements from the supplied XHTML.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML with script elements removed.</returns>
    public static string Remove(string xhtml) => ScriptElementPattern().Replace(xhtml, string.Empty);

    #endregion

    #region Private Methods

    /// <summary>Creates a case-insensitive match for complete script elements.</summary>
    /// <returns>A regex that matches script elements and their contents.</returns>
    [GeneratedRegex("""<script\b[^>]*>[\s\S]*?</script\s*>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptElementPattern();

    #endregion
}
