using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Rewrites the nested 3-column table Cerner uses for bullet lists to explicit
/// bullet paragraphs with hanging indents.
/// </summary>
/// <remarks>
/// Cerner never emits <c>ul</c>/<c>li</c>. A bullet list arrives as a table whose
/// rows hold a fixed-width marker cell, a fixed-width spacer cell and a widthless
/// text cell, itself nested three tables deep. Telerik exports that depth as
/// <c>\itap3</c> cell content and emits no <c>\trowd</c> or <c>\cellx</c> row
/// definitions for it, so the item text reaches the RTF but a reader has no column
/// widths to lay it out. Telerik's RTF list definition also maps bullets to a Symbol
/// private-use character that Word may not display. Explicit U+2022 bullet paragraphs
/// avoid both defects. The rewrite is kept deliberately narrow - only a leaf table in
/// which every row follows that exact
/// marker/spacer/text shape is touched - so genuine data tables pass through
/// byte-identical.
/// </remarks>
internal static partial class XhtmlBulletTableToListConverter
{
    private const int MaxPasses = 32;

    #region Public Methods

    /// <summary>Converts qualifying Cerner bullet tables to unordered lists.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML with bullet tables replaced by lists, or the input unchanged.</returns>
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

    /// <summary>Converts every qualifying leaf table in one pass.</summary>
    /// <param name="xhtml">The XHTML input to process.</param>
    /// <returns>The XHTML after one pass, or the input unchanged.</returns>
    private static string ConvertPass(string xhtml)
    {
        MatchCollection tags = XhtmlMarkup.TagPattern().Matches(xhtml);
        List<(int Start, int End, string Replacement)> rewrites = new();

        for (int index = 0; index < tags.Count; index++)
        {
            Match opening = tags[index];
            if (!IsOpeningTag(opening, "table"))
                continue;

            int closingIndex = FindMatchingClose(tags, index);
            if (closingIndex < 0)
                continue;

            Match closing = tags[closingIndex];
            if (TryBuildList(xhtml, tags, index, closingIndex, out string? list))
            {
                rewrites.Add((opening.Index, closing.Index + closing.Length, list!));
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

    /// <summary>Builds the replacement list when the table qualifies; leaves it otherwise.</summary>
    /// <param name="xhtml">The XHTML being processed.</param>
    /// <param name="tags">The pre-matched markup tokens.</param>
    /// <param name="openIndex">The index of the table's opening tag.</param>
    /// <param name="closeIndex">The index of the table's closing tag.</param>
    /// <param name="list">The replacement <c>ul</c> markup when the table qualifies.</param>
    /// <returns><see langword="true"/> when the table is a Cerner bullet table.</returns>
    private static bool TryBuildList(
        string xhtml,
        MatchCollection tags,
        int openIndex,
        int closeIndex,
        out string? list)
    {
        list = null;

        for (int index = openIndex + 1; index < closeIndex; index++)
        {
            Group name = tags[index].Groups["name"];
            if (name.Success &&
                (name.Value.Equals("table", StringComparison.OrdinalIgnoreCase) ||
                 name.Value.Equals("thead", StringComparison.OrdinalIgnoreCase) ||
                 name.Value.Equals("th", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        List<(Match Row, Match RowClose, List<(Match Tag, Match Close)> Cells)> rows = new();
        for (int index = openIndex + 1; index < closeIndex; index++)
        {
            Match tag = tags[index];
            if (!IsOpeningTag(tag, "tr"))
                continue;

            int rowClose = FindMatchingClose(tags, index);
            if (rowClose < 0 || rowClose >= closeIndex)
                return false;

            List<(Match Tag, Match Close)> cells = CollectCells(tags, index, rowClose);
            if (cells.Count != 3)
                return false;

            foreach ((Match cellTag, _) in cells)
            {
                if (HasNonUnitSpan(cellTag.Groups["attributes"].Value))
                    return false;
            }

            rows.Add((tag, tags[rowClose], cells));
            index = rowClose;
        }

        if (rows.Count == 0)
            return false;

        if (!OnlyStructureBetweenRows(xhtml, tags[openIndex], tags[closeIndex], rows))
            return false;

        StringBuilder markup = new();
        foreach ((_, _, List<(Match Tag, Match Close)> cells) in rows)
        {
            string markerText = CellText(xhtml, cells[0]);
            if (!IsMarker(markerText))
                return false;

            if (CellText(xhtml, cells[1]).Length != 0)
                return false;

            markup.Append("<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;");
            markup.Append(UnwrapSingleDiv(CellInnerMarkup(xhtml, cells[2])));
            markup.Append("</div>");
        }
        list = markup.ToString();
        return true;
    }

    /// <summary>Collects the direct <c>td</c> cells of a table row.</summary>
    /// <param name="tags">The pre-matched markup tokens.</param>
    /// <param name="rowIndex">The index of the row's opening tag.</param>
    /// <param name="rowClose">The index of the row's closing tag.</param>
    /// <returns>The row's cells, each as its opening and closing tag.</returns>
    private static List<(Match Tag, Match Close)> CollectCells(
        MatchCollection tags, int rowIndex, int rowClose)
    {
        List<(Match Tag, Match Close)> cells = new();
        for (int index = rowIndex + 1; index < rowClose; index++)
        {
            Match tag = tags[index];
            if (!IsOpeningTag(tag, "td"))
                continue;

            int cellClose = FindMatchingClose(tags, index);
            if (cellClose < 0 || cellClose >= rowClose)
                return [];

            cells.Add((tag, tags[cellClose]));
            index = cellClose;
        }

        return cells;
    }

    /// <summary>Finds the closing tag matching an opening tag at <paramref name="openIndex"/>.</summary>
    /// <param name="tags">The pre-matched markup tokens.</param>
    /// <param name="openIndex">The index of the opening tag.</param>
    /// <returns>The index of the matching closing tag, or -1 for unbalanced markup.</returns>
    private static int FindMatchingClose(MatchCollection tags, int openIndex)
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
    private static bool IsOpeningTag(Match tag, string name) =>
        tag.Groups["name"].Success &&
        !tag.Groups["closing"].Success &&
        tag.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase) &&
        !tag.Groups["attributes"].Value.TrimEnd().EndsWith('/');

    /// <summary>
    /// Verifies the table holds nothing outside its rows that the rewrite would drop:
    /// only whitespace and <c>tbody</c>/<c>colgroup</c>/<c>col</c> markup are allowed
    /// between the table's tags and around the rows.
    /// </summary>
    /// <param name="xhtml">The XHTML being processed.</param>
    /// <param name="tableOpen">The table's opening tag.</param>
    /// <param name="tableClose">The table's closing tag.</param>
    /// <param name="rows">The collected rows with their tag ranges.</param>
    /// <returns><see langword="true"/> when nothing but row-structural markup sits between rows.</returns>
    private static bool OnlyStructureBetweenRows(
        string xhtml,
        Match tableOpen,
        Match tableClose,
        List<(Match Row, Match RowClose, List<(Match Tag, Match Close)> Cells)> rows)
    {
        int gapStart = tableOpen.Index + tableOpen.Length;
        foreach ((Match row, Match rowClose, _) in rows)
        {
            if (!IsStructuralMarkupOnly(xhtml.Substring(gapStart, row.Index - gapStart)))
                return false;

            gapStart = rowClose.Index + rowClose.Length;
        }

        return IsStructuralMarkupOnly(xhtml.Substring(gapStart, tableClose.Index - gapStart));
    }

    /// <summary>Determines whether a gap holds only whitespace and row-structural tags.</summary>
    /// <param name="gap">The text between two row boundaries.</param>
    /// <returns><see langword="true"/> for whitespace plus <c>tbody</c>/<c>colgroup</c>/<c>col</c> tags.</returns>
    private static bool IsStructuralMarkupOnly(string gap)
    {
        int coveredThrough = 0;
        foreach (Match tag in XhtmlMarkup.TagPattern().Matches(gap))
        {
            if (!tag.Groups["name"].Success ||
                !StructuralElements.Contains(tag.Groups["name"].Value) ||
                !gap.AsSpan(coveredThrough, tag.Index - coveredThrough).IsWhiteSpace())
            {
                return false;
            }

            coveredThrough = tag.Index + tag.Length;
        }

        return gap.AsSpan(coveredThrough).IsWhiteSpace();
    }

    /// <summary>Determines whether a cell spans more than one row or column.</summary>
    /// <param name="attributes">The cell's attribute text.</param>
    /// <returns><see langword="true"/> when a <c>colspan</c> or <c>rowspan</c> is not <c>1</c>.</returns>
    private static bool HasNonUnitSpan(string attributes)
    {
        foreach (Match attribute in SpanAttributePattern().Matches(attributes))
        {
            string value = attribute.Groups["double"].Success ? attribute.Groups["double"].Value
                : attribute.Groups["single"].Success ? attribute.Groups["single"].Value
                : attribute.Groups["unquoted"].Value;
            if (value.Trim() != "1")
                return true;
        }

        return false;
    }

    /// <summary>Returns the markup between a cell's opening and closing tags.</summary>
    /// <param name="xhtml">The XHTML being processed.</param>
    /// <param name="cell">The cell's opening and closing tags.</param>
    /// <returns>The cell's inner markup.</returns>
    private static string CellInnerMarkup(string xhtml, (Match Tag, Match Close) cell) =>
        xhtml.Substring(
            cell.Tag.Index + cell.Tag.Length,
            cell.Close.Index - cell.Tag.Index - cell.Tag.Length);

    /// <summary>Returns a cell's text with tags stripped and list marker entities decoded.</summary>
    /// <param name="xhtml">The XHTML being processed.</param>
    /// <param name="cell">The cell's opening and closing tags.</param>
    /// <returns>The cell's normalized text.</returns>
    private static string CellText(string xhtml, (Match Tag, Match Close) cell)
    {
        string inner = XhtmlMarkup.TagPattern().Replace(CellInnerMarkup(xhtml, cell), string.Empty);
        string decoded = DecodeMarkerEntities(inner);
        return decoded.Trim();
    }

    /// <summary>Decodes the entities a Cerner marker or spacer cell can carry.</summary>
    /// <param name="text">The cell's stripped text.</param>
    /// <returns>The text with bullet, middot, non-breaking-space and numeric entities decoded.</returns>
    private static string DecodeMarkerEntities(string text)
    {
        string decoded = text
            .Replace("&bull;", "•", StringComparison.OrdinalIgnoreCase)
            .Replace("&middot;", "·", StringComparison.OrdinalIgnoreCase)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);
        return NumericEntityPattern().Replace(decoded, match =>
        {
            string digits = match.Groups["digits"].Value;
            int code = digits.StartsWith("x", StringComparison.OrdinalIgnoreCase)
                ? System.Convert.ToInt32(digits[1..], 16)
                : int.Parse(digits, CultureInfo.InvariantCulture);
            return code is >= 0 and <= 0x10FFFF ? char.ConvertFromUtf32(code) : match.Value;
        });
    }

    /// <summary>Determines whether a marker cell holds exactly one bullet character.</summary>
    /// <param name="text">The marker cell's normalized text.</param>
    /// <returns><see langword="true"/> for a single bullet, middot or <c>o</c> character.</returns>
    private static bool IsMarker(string text) =>
        text.Length == 1 && text[0] is '•' or '◦' or '▪' or '·' or 'o' or 'O';

    /// <summary>Unwraps content that is exactly one top-level <c>div</c>.</summary>
    /// <param name="markup">The text cell's inner markup.</param>
    /// <returns>The div's inner markup, or the original markup when it is not a lone div.</returns>
    private static string UnwrapSingleDiv(string markup)
    {
        string trimmed = markup.Trim();
        MatchCollection tags = XhtmlMarkup.TagPattern().Matches(trimmed);
        if (tags.Count == 0 || !IsOpeningTag(tags[0], "div") || tags[0].Index != 0)
            return markup;

        int divClose = FindMatchingClose(tags, 0);
        if (divClose != tags.Count - 1)
            return markup;

        Match close = tags[divClose];
        if (close.Index + close.Length != trimmed.Length)
            return markup;

        return trimmed.Substring(tags[0].Length, close.Index - tags[0].Length);
    }

    /// <summary>Creates matches for <c>colspan</c> and <c>rowspan</c> attributes.</summary>
    /// <returns>A regex that captures quoted or unquoted span values.</returns>
    [GeneratedRegex("""(?:^|\s)(?:colspan|rowspan)\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<unquoted>[^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpanAttributePattern();

    /// <summary>Creates matches for decimal and hexadecimal numeric character references.</summary>
    /// <returns>A regex that captures the numeric part of an entity.</returns>
    [GeneratedRegex("""&#(?<digits>[0-9]+|[xX][0-9A-Fa-f]+);""")]
    private static partial Regex NumericEntityPattern();

    private static readonly HashSet<string> StructuralElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "tbody", "colgroup", "col"
    };

    #endregion
}
