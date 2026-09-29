using System.Text;
using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Restores <c>line-through</c> that a descendant's <c>text-decoration</c>
/// declaration would otherwise reset.
/// </summary>
/// <remarks>
/// In CSS, <c>text-decoration</c> does not inherit: the ancestor's box paints the
/// decoration over its descendants, so a descendant that redeclares the property
/// adds to the ancestor's lines rather than replacing them. Telerik's importer
/// instead treats the declaration as a reset of every decoration flag, so an
/// ancestor's <c>line-through</c> is silently dropped - and a run can only ever
/// carry one decoration keyword. This pass wraps the redeclaring element's inner
/// content in <c>&lt;s&gt;</c> (which Telerik's own stylesheet maps to
/// <c>line-through</c>) whenever <c>line-through</c> is painted by an ancestor
/// but not declared on the element. Elements that declare nothing inherit the
/// strike correctly and are left alone.
/// The trade is deliberate: the redeclared decoration (usually <c>underline</c>) is
/// lost inside the wrap, because a dropped strike changes clinical meaning - it
/// marks retracted content - while a dropped underline is cosmetic. Telerik's
/// importer collapses a run to a single decoration even though <c>Run.Underline</c>
/// and <c>Run.Strikethrough</c> are independent in the document model, so a
/// post-import <c>RadFlowDocument</c> fixup could carry both; that is the
/// follow-up if ever needed.
/// </remarks>
internal static partial class XhtmlTextDecorationNormalizer
{
    #region Public Methods

    /// <summary>Wraps redeclaring elements' content in <c>s</c> to keep an ancestor's strike.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML with <c>&lt;s&gt;</c> wraps added, or the input unchanged.</returns>
    public static string Normalize(string xhtml)
    {
        MatchCollection tags = XhtmlMarkup.TagPattern().Matches(xhtml);
        List<OpenElement> openElements = new();
        List<(int Position, string Markup)> insertions = new();

        foreach (Match tag in tags)
        {
            Group name = tag.Groups["name"];
            if (!name.Success)
                continue;

            if (tag.Groups["closing"].Success)
            {
                PopToMatchingElement(openElements, insertions, tag, name.Value);
                continue;
            }

            Group attributes = tag.Groups["attributes"];
            bool selfContained = IsSelfContained(name.Value, attributes.Value);

            HashSet<string> inherited = openElements.Count > 0
                ? openElements[^1].Decorations
                : [];

            GetDeclaredDecorations(attributes.Value, out bool declared, out HashSet<string> ownTokens);

            OpenElement element = new(name.Value) { Decorations = new HashSet<string>(inherited, StringComparer.OrdinalIgnoreCase) };
            element.Decorations.UnionWith(ownTokens);

            if (declared && inherited.Contains("line-through") &&
                !ownTokens.Contains("line-through") && !selfContained &&
                !ContentAlreadyStruck(xhtml, tag))
            {
                element.SOpenPosition = tag.Index + tag.Length;
                element.Decorations.Add("line-through");
            }

            if (!selfContained)
                openElements.Add(element);
        }

        if (insertions.Count == 0)
            return xhtml;

        StringBuilder result = new(xhtml.Length + insertions.Count * 7);
        int copiedThrough = 0;
        foreach ((int position, string markup) in insertions.OrderBy(entry => entry.Position))
        {
            result.Append(xhtml, copiedThrough, position - copiedThrough);
            result.Append(markup);
            copiedThrough = position;
        }

        result.Append(xhtml, copiedThrough, xhtml.Length - copiedThrough);
        return result.ToString();
    }

    #endregion

    #region Private Methods

    /// <summary>Reads the recognised decoration keywords an element declares inline.</summary>
    /// <param name="attributes">The tag's attribute text.</param>
    /// <param name="declared">Whether a decoration property is declared at all.</param>
    /// <param name="ownTokens">The recognised keywords across all decoration declarations.</param>
    private static void GetDeclaredDecorations(
        string attributes,
        out bool declared,
        out HashSet<string> ownTokens)
    {
        declared = false;
        ownTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Match styleAttribute = XhtmlMarkup.StyleAttributePattern().Match(attributes);
        if (!styleAttribute.Success)
            return;

        Group styleValue = styleAttribute.Groups["double"].Success ? styleAttribute.Groups["double"]
            : styleAttribute.Groups["single"].Success ? styleAttribute.Groups["single"]
            : styleAttribute.Groups["unquoted"];
        if (!styleValue.Success)
            return;

        foreach (Match declaration in DecorationDeclarationPattern().Matches(styleValue.Value))
        {
            declared = true;
            foreach (Match token in DecorationKeywordPattern().Matches(declaration.Groups["value"].Value))
                ownTokens.Add(token.Value);
        }
    }

    /// <summary>Pops open elements through the matching close, emitting pending wraps.</summary>
    /// <param name="openElements">The stack of currently open elements.</param>
    /// <param name="insertions">The wrap insertions collected so far.</param>
    /// <param name="closing">The closing tag being processed.</param>
    /// <param name="name">The closing tag's element name.</param>
    private static void PopToMatchingElement(
        List<OpenElement> openElements,
        List<(int Position, string Markup)> insertions,
        Match closing,
        string name)
    {
        for (int index = openElements.Count - 1; index >= 0; index--)
        {
            if (openElements[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (openElements[index].SOpenPosition.HasValue)
                {
                    insertions.Add((openElements[index].SOpenPosition!.Value, "<s>"));
                    insertions.Add((closing.Index, "</s>"));
                }

                openElements.RemoveRange(index, openElements.Count - index);
                return;
            }
        }
    }

    /// <summary>Determines whether the element's content already begins with an <c>s</c> wrap.</summary>
    /// <param name="xhtml">The XHTML being processed.</param>
    /// <param name="opening">The element's opening tag.</param>
    /// <returns><see langword="true"/> when the content's first tag is an <c>s</c> element.</returns>
    private static bool ContentAlreadyStruck(string xhtml, Match opening)
    {
        string trimmed = xhtml[(opening.Index + opening.Length)..].TrimStart();
        Match first = XhtmlMarkup.TagPattern().Match(trimmed);
        return first.Success && first.Index == 0 && IsOpeningTag(first, "s");
    }

    /// <summary>Determines whether a match is an opening tag of the given element.</summary>
    /// <param name="tag">The parsed XHTML tag.</param>
    /// <param name="name">The element name to compare.</param>
    /// <returns><see langword="true"/> for a non-closing, non-self-closing tag of that name.</returns>
    private static bool IsOpeningTag(Match tag, string name) =>
        tag.Groups["name"].Success &&
        !tag.Groups["closing"].Success &&
        tag.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase) &&
        !tag.Groups["attributes"].Value.TrimEnd().EndsWith('/');

    /// <summary>Determines whether an element can never carry children.</summary>
    /// <param name="name">The element name.</param>
    /// <param name="attributes">The tag's attribute text.</param>
    /// <returns><see langword="true"/> for a void or self-closing element.</returns>
    private static bool IsSelfContained(string name, string attributes) =>
        attributes.TrimEnd().EndsWith('/') || VoidElements.Contains(name);

    /// <summary>Creates matches for <c>text-decoration</c> declarations inside a style value.</summary>
    /// <returns>A regex that captures a declaration prefix and its value.</returns>
    [GeneratedRegex("""(?:^|;)\s*text-decoration(?:-line)?\s*:\s*(?<value>[^;]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecorationDeclarationPattern();

    /// <summary>Creates matches for recognised decoration keywords.</summary>
    /// <returns>A regex that matches <c>underline</c>, <c>overline</c> and <c>line-through</c>.</returns>
    [GeneratedRegex("""\b(?:underline|overline|line-through)\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecorationKeywordPattern();

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"
    };

    /// <summary>An element on the open-element stack, with any pending <c>s</c> wrap position.</summary>
    private sealed class OpenElement(string name)
    {
        public string Name { get; } = name;

        public HashSet<string> Decorations { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public int? SOpenPosition { get; set; }
    }

    #endregion
}
