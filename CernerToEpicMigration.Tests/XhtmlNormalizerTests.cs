using CernerToEpicMigration.Processing;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// Pure string-in, string-out coverage of the two XHTML pre-processing passes:
/// <see cref="XhtmlBulletTableToListConverter"/> and <see cref="XhtmlTextDecorationNormalizer"/>.
/// </summary>
public class XhtmlNormalizerTests
{
    private const string BulletRow =
        """<tr><td valign="top" style="text-align: right; white-space: nowrap;" width="24"><div><span style="font: 1em serif;">&#8226;</span></div></td><td width="8"> </td><td valign="top"><div>{0}</div></td></tr>""";

    private static string CernerBulletTable(params string[] items) =>
        "<div> <table style=\"table-layout: fixed; border-collapse: collapse;\" valign=\"top\"><tbody><tr><td valign=\"top\">" +
        "<div style=\"margin-top: 1em; margin-bottom: 1em; margin: 0px; padding-left: 15px;\">" +
        "<table width=\"100%\"><tbody>" +
        string.Concat(items.Select(item => string.Format(BulletRow, item))) +
        "</tbody></table>" +
        "</div>" +
        "</td></tr></tbody></table> </div>";

    [Fact]
    public void A_cerner_bullet_table_becomes_explicit_bullet_paragraphs()
    {
        string xhtml = CernerBulletTable("Knee replacement (2004)", "Hip replacement (2010)");

        string result = XhtmlBulletTableToListConverter.Convert(xhtml);

        Assert.Contains("<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;Knee replacement (2004)</div>", result, StringComparison.Ordinal);
        Assert.Contains("<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;Hip replacement (2010)</div>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<ul>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("width=\"8\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_genuine_three_column_data_table_is_returned_unchanged()
    {
        string xhtml = "<table><tr><td>Knee</td><td> </td><td>2004</td></tr><tr><td>Hip</td><td> </td><td>2010</td></tr></table>";

        Assert.Equal(xhtml, XhtmlBulletTableToListConverter.Convert(xhtml));
    }

    [Fact]
    public void A_table_with_a_colspan_is_returned_unchanged()
    {
        string xhtml = "<table><tr><td>&#8226;</td><td colspan=\"2\">spans two cells</td></tr></table>";

        Assert.Equal(xhtml, XhtmlBulletTableToListConverter.Convert(xhtml));
    }

    [Fact]
    public void A_table_holding_a_non_qualifying_nested_table_is_returned_unchanged()
    {
        string xhtml = "<table><tr><td>&#8226;</td><td> </td><td><table><tr><td>nested</td></tr></table></td></tr></table>";

        Assert.Equal(xhtml, XhtmlBulletTableToListConverter.Convert(xhtml));
    }

    [Fact]
    public void Nested_bullet_tables_both_convert()
    {
        string innerRow =
            """<tr><td width="24">&#8226;</td><td width="8"> </td><td><div>inner item</div></td></tr>""";
        string innerTable = "<table><tbody>" + innerRow + "</tbody></table>";
        string outerRow =
            "<tr><td width=\"24\">&#8226;</td><td width=\"8\"> </td><td><div>" + innerTable + "</div></td></tr>";
        string xhtml = "<table><tbody>" + outerRow + "</tbody></table>";

        string result = XhtmlBulletTableToListConverter.Convert(xhtml);

        Assert.Equal("<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;inner item</div></div>", result);
    }

    [Fact]
    public void A_table_with_a_caption_is_returned_unchanged()
    {
        string xhtml = "<table><caption>Procedures</caption><tr><td>&#8226;</td><td> </td><td>item</td></tr></table>";

        Assert.Equal(xhtml, XhtmlBulletTableToListConverter.Convert(xhtml));
    }

    [Fact]
    public void The_bullet_table_conversion_is_idempotent()
    {
        string xhtml = CernerBulletTable("Knee replacement (2004)");

        string once = XhtmlBulletTableToListConverter.Convert(xhtml);

        Assert.Equal(once, XhtmlBulletTableToListConverter.Convert(once));
    }

    [Fact]
    public void A_cerner_unordered_list_becomes_explicit_bullet_paragraphs()
    {
        string xhtml =
            """<div class="ddemrcontent" id="_c72f" dd:contenttype="PROCEDURES"><ul style="margin: 0px; padding-left: 15px; list-style-type: disc;" xmlns:dd="DynamicDocumentation"><li class="ddemrcontentitem ddremovable" id="_6a33" dd:contenttype="PROCEDURES" dd:entityid="2612261207">Colonoscopy, flexible; with biopsy, single or multiple (04/26/2023)</li><li class="ddemrcontentitem ddremovable" id="_8ac5" dd:contenttype="PROCEDURES" dd:entityid="2612447193">EGD - Esophagogastroduodenoscopy</li></ul></div>""";

        string result = XhtmlUnorderedListToBulletParagraphConverter.Convert(xhtml);

        Assert.Contains("<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;Colonoscopy, flexible; with biopsy, single or multiple (04/26/2023)</div>", result, StringComparison.Ordinal);
        Assert.Contains("<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;EGD - Esophagogastroduodenoscopy</div>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<ul", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<li", result, StringComparison.Ordinal);
        Assert.Contains("""<div class="ddemrcontent" id="_c72f" dd:contenttype="PROCEDURES">""", result, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unstyled_unordered_list_uses_the_disc_marker()
    {
        string xhtml = "<ul><li>a</li></ul>";

        Assert.Equal(
            "<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;a</div>",
            XhtmlUnorderedListToBulletParagraphConverter.Convert(xhtml));
    }

    [Fact]
    public void Circle_and_square_markers_map_to_their_unicode_bullets()
    {
        Assert.Equal(
            "<div style=\"margin-left: 32px; text-indent: -17px;\">&#9702;&#160;a</div>",
            XhtmlUnorderedListToBulletParagraphConverter.Convert("""<ul style="list-style-type: circle;"><li>a</li></ul>"""));

        Assert.Equal(
            "<div style=\"margin-left: 32px; text-indent: -17px;\">&#9642;&#160;a</div>",
            XhtmlUnorderedListToBulletParagraphConverter.Convert("""<ul style="list-style-type: square;"><li>a</li></ul>"""));

        Assert.Equal(
            "<div style=\"margin-left: 32px; text-indent: -17px;\">&#9642;&#160;a</div>",
            XhtmlUnorderedListToBulletParagraphConverter.Convert("""<ul style="list-style: square inside;"><li>a</li></ul>"""));
    }

    [Fact]
    public void A_list_with_marker_none_is_returned_unchanged()
    {
        string xhtml = """<ul style="list-style-type: none;"><li>a</li></ul>""";

        Assert.Equal(xhtml, XhtmlUnorderedListToBulletParagraphConverter.Convert(xhtml));
    }

    [Fact]
    public void A_list_with_stray_content_between_items_is_returned_unchanged()
    {
        string strayText = "<ul><li>a</li> stray <li>b</li></ul>";
        string strayMarkup = "<ul><li>a</li><p>note</p><li>b</li></ul>";

        Assert.Equal(strayText, XhtmlUnorderedListToBulletParagraphConverter.Convert(strayText));
        Assert.Equal(strayMarkup, XhtmlUnorderedListToBulletParagraphConverter.Convert(strayMarkup));
    }

    [Fact]
    public void Nested_unordered_lists_convert_innermost_first()
    {
        string xhtml = "<ul><li>outer<ul><li>inner</li></ul></li></ul>";

        string result = XhtmlUnorderedListToBulletParagraphConverter.Convert(xhtml);

        Assert.Equal("<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;outer<div style=\"margin-left: 32px; text-indent: -17px;\">&#8226;&#160;inner</div></div>", result);
    }

    [Fact]
    public void The_unordered_list_conversion_is_idempotent()
    {
        string xhtml = """<ul style="list-style-type: disc;"><li>a</li><li>b</li></ul>""";

        string once = XhtmlUnorderedListToBulletParagraphConverter.Convert(xhtml);

        Assert.Equal(once, XhtmlUnorderedListToBulletParagraphConverter.Convert(once));
    }

    [Fact]
    public void A_descendant_decoration_gains_the_keywords_its_ancestors_paint()
    {
        string xhtml =
            """<span style="font-size: 16pt;"><span style="color: rgb(243, 156, 18);"><span style="text-decoration: line-through;"><span style="font-weight: bold;"><span style="font-style: italic;"><span style="text-decoration: underline;">Test the sentence </span></span></span></span></span></span>""";

        string result = XhtmlTextDecorationNormalizer.Normalize(xhtml);

        Assert.Contains("""text-decoration: underline;"><s>Test the sentence </s>""", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_nested_line_through_declaration_is_returned_unchanged()
    {
        string xhtml =
            """<p><span style="text-decoration: line-through;">removed text</span></p>""";

        Assert.Equal(xhtml, XhtmlTextDecorationNormalizer.Normalize(xhtml));
    }

    [Fact]
    public void An_ancestor_declaring_none_contributes_nothing_to_its_descendants()
    {
        string xhtml =
            """<div style="text-decoration: none;"><span style="text-decoration: underline;">underlined</span></div>""";

        Assert.Equal(xhtml, XhtmlTextDecorationNormalizer.Normalize(xhtml));
    }

    [Fact]
    public void A_descendant_declaring_none_still_paints_the_ancestors_lines()
    {
        string xhtml =
            """<span style="text-decoration: line-through;"><span style="text-decoration: none;">still struck</span></span>""";

        string result = XhtmlTextDecorationNormalizer.Normalize(xhtml);

        Assert.Contains("""text-decoration: none;"><s>still struck</s>""", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_void_elements_decoration_does_not_leak_to_its_siblings()
    {
        string xhtml =
            """<p><img style="text-decoration: line-through;" src="x.png"><span style="text-decoration: underline;">underlined</span></p>""";

        Assert.Equal(xhtml, XhtmlTextDecorationNormalizer.Normalize(xhtml));
    }

    [Fact]
    public void An_empty_named_anchor_is_removed()
    {
        string xhtml = """<div><a name="_2ae31cb6-bf8e-4960-beb2-1162a8181340"></a><div>Care Plan</div></div>""";

        Assert.Equal("<div><div>Care Plan</div></div>", XhtmlEmptyAnchorRemover.Remove(xhtml));
    }

    [Fact]
    public void Self_closing_and_whitespace_only_anchors_are_removed()
    {
        string xhtml = """<p><A NAME="a"/>one<a name="b"> </a>two</p>""";

        Assert.Equal("<p>onetwo</p>", XhtmlEmptyAnchorRemover.Remove(xhtml));
    }

    [Fact]
    public void An_anchor_with_an_href_is_returned_unchanged()
    {
        string xhtml = """<p><a href="#_x" name="y"></a><a href="https://example.org">link</a></p>""";

        Assert.Equal(xhtml, XhtmlEmptyAnchorRemover.Remove(xhtml));
    }

    [Fact]
    public void A_named_anchor_with_content_is_returned_unchanged()
    {
        string xhtml = """<p><a name="x">visible</a><a name="y"><span></span></a></p>""";

        Assert.Equal(xhtml, XhtmlEmptyAnchorRemover.Remove(xhtml));
    }

    [Fact]
    public void A_data_href_attribute_does_not_count_as_an_address()
    {
        string xhtml = """<p><a name="x" data-href="#y"></a>text</p>""";

        Assert.Equal("<p>text</p>", XhtmlEmptyAnchorRemover.Remove(xhtml));
    }

    [Fact]
    public void The_empty_anchor_removal_is_idempotent()
    {
        string xhtml = """<div><a name="a"></a>x<a href="#b">y</a></div>""";

        string once = XhtmlEmptyAnchorRemover.Remove(xhtml);

        Assert.Equal(once, XhtmlEmptyAnchorRemover.Remove(once));
    }

    [Fact]
    public void The_decoration_normalization_is_idempotent()
    {
        string xhtml =
            """<span style="text-decoration: line-through;"><span style="text-decoration: underline;">Test the sentence </span></span>""";

        string once = XhtmlTextDecorationNormalizer.Normalize(xhtml);

        Assert.Equal(once, XhtmlTextDecorationNormalizer.Normalize(once));
    }
}
