using System.Text;
using CernerToEpicMigration.Processing;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// Decoding is where clinical text quietly gets corrupted, so each supported way of
/// declaring an encoding is covered.
/// </summary>
public class XhtmlDocumentReaderTests
{
    private const string ClinicalText = "Café — 37°C, 5 µg/mL, Dr. Müller";

    static XhtmlDocumentReaderTests() => XhtmlDocumentReader.RegisterLegacyCodePages();

    [Fact]
    public void Utf8_with_bom_is_decoded_and_the_bom_is_stripped()
    {
        string xhtml = $"<html><body><p>{ClinicalText}</p></body></html>";
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(xhtml);

        XhtmlDocument document = XhtmlDocumentReader.Decode(bytes);

        Assert.Equal(xhtml, document.Text);
        Assert.StartsWith("<html>", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8_without_bom_or_declaration_is_decoded()
    {
        string xhtml = $"<html><body><p>{ClinicalText}</p></body></html>";
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xhtml);

        Assert.Equal(xhtml, XhtmlDocumentReader.Decode(bytes).Text);
    }

    [Fact]
    public void Windows_1252_declared_in_the_xml_declaration_is_honoured()
    {
        Encoding windows1252 = Encoding.GetEncoding("windows-1252");
        string xhtml = $"""<?xml version="1.0" encoding="windows-1252"?><html><body><p>Café 37°C</p></body></html>""";
        byte[] bytes = windows1252.GetBytes(xhtml);

        XhtmlDocument document = XhtmlDocumentReader.Decode(bytes);

        Assert.Contains("Café 37°C", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('�', document.Text);
    }

    [Fact]
    public void Charset_declared_in_a_meta_tag_is_honoured()
    {
        Encoding latin1 = Encoding.GetEncoding("iso-8859-1");
        string xhtml = """<html><head><meta charset="iso-8859-1"></head><body><p>Café</p></body></html>""";

        XhtmlDocument document = XhtmlDocumentReader.Decode(latin1.GetBytes(xhtml));

        Assert.Contains("Café", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_bytes_with_no_declaration_fall_back_instead_of_corrupting()
    {
        // 0xE9 is 'é' in windows-1252 and invalid on its own in UTF-8.
        byte[] bytes = Encoding.GetEncoding("windows-1252").GetBytes("<html><body><p>Café</p></body></html>");

        XhtmlDocument document = XhtmlDocumentReader.Decode(bytes);

        Assert.Contains("Café", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('�', document.Text);
    }

    [Fact]
    public void Utf16_with_bom_is_decoded()
    {
        string xhtml = $"<html><body><p>{ClinicalText}</p></body></html>";
        byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xhtml)).ToArray();

        Assert.Equal(xhtml, XhtmlDocumentReader.Decode(bytes).Text);
    }

    [Fact]
    public void An_unknown_charset_name_does_not_throw()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            """<?xml version="1.0" encoding="not-a-real-charset"?><html><body><p>plain</p></body></html>""");

        Assert.Contains("plain", XhtmlDocumentReader.Decode(bytes).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Byte_count_reports_the_bytes_on_disk()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("<html><body><p>size</p></body></html>");

        Assert.Equal(bytes.Length, XhtmlDocumentReader.Decode(bytes).ByteCount);
    }
}
