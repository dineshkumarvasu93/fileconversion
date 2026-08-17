using System.Text;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Processing;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// Every input document arrives Base64-encoded, so this is the first thing that can go
/// wrong with a file - and the reason it lands in the error folder.
/// </summary>
public class Base64InputDecoderTests
{
    private const string Xhtml = """<?xml version="1.0" encoding="utf-8"?><html><body><p>Progress Note</p></body></html>""";

    static Base64InputDecoderTests() => XhtmlDocumentReader.RegisterLegacyCodePages();

    [Fact]
    public void A_base64_envelope_yields_the_original_bytes()
    {
        byte[] payload = new UTF8Encoding(false).GetBytes(Xhtml);
        byte[] envelope = Encoding.ASCII.GetBytes(Convert.ToBase64String(payload));

        Assert.Equal(payload, Base64InputDecoder.Decode(envelope));
    }

    [Fact]
    public void Line_wrapped_base64_is_accepted()
    {
        byte[] payload = new UTF8Encoding(false).GetBytes(Xhtml);
        string wrapped = Convert.ToBase64String(payload, Base64FormattingOptions.InsertLineBreaks);

        Assert.Contains('\n', wrapped);
        Assert.Equal(payload, Base64InputDecoder.Decode(Encoding.ASCII.GetBytes(wrapped)));
    }

    [Fact]
    public void An_envelope_saved_with_a_byte_order_mark_is_accepted()
    {
        byte[] payload = new UTF8Encoding(false).GetBytes(Xhtml);
        byte[] envelope = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.ASCII.GetBytes(Convert.ToBase64String(payload)))
            .ToArray();

        Assert.Equal(payload, Base64InputDecoder.Decode(envelope));
    }

    [Fact]
    public void Trailing_whitespace_is_ignored()
    {
        byte[] payload = new UTF8Encoding(false).GetBytes(Xhtml);
        byte[] envelope = Encoding.ASCII.GetBytes(Convert.ToBase64String(payload) + "\r\n\r\n");

        Assert.Equal(payload, Base64InputDecoder.Decode(envelope));
    }

    [Fact]
    public void The_decoded_payload_keeps_its_own_encoding()
    {
        Encoding windows1252 = Encoding.GetEncoding("windows-1252");
        string xhtml = """<?xml version="1.0" encoding="windows-1252"?><html><body><p>Café 37°C</p></body></html>""";
        byte[] envelope = Encoding.ASCII.GetBytes(Convert.ToBase64String(windows1252.GetBytes(xhtml)));

        XhtmlDocument document = XhtmlDocumentReader.Decode(Base64InputDecoder.Decode(envelope));

        Assert.Contains("Café 37°C", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('�', document.Text);
    }

    [Fact]
    public void Unwrapped_markup_is_rejected_rather_than_converted()
    {
        Assert.Throws<Base64DecodingException>(
            () => Base64InputDecoder.Decode(Encoding.UTF8.GetBytes(Xhtml)));
    }

    [Fact]
    public void An_empty_file_is_rejected()
    {
        Assert.Throws<Base64DecodingException>(() => Base64InputDecoder.Decode([]));
    }

    [Fact]
    public void A_whitespace_only_file_is_rejected_instead_of_decoding_to_nothing()
    {
        Assert.Throws<Base64DecodingException>(
            () => Base64InputDecoder.Decode(Encoding.ASCII.GetBytes("   \r\n  \r\n")));
    }

    [Fact]
    public void A_truncated_envelope_is_rejected()
    {
        // Base64 is four characters to three bytes; dropping one breaks the last group.
        string truncated = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(Xhtml))[..^1];

        Assert.Throws<Base64DecodingException>(
            () => Base64InputDecoder.Decode(Encoding.ASCII.GetBytes(truncated)));
    }

    [Fact]
    public void A_non_ascii_byte_in_the_envelope_is_rejected()
    {
        byte[] envelope = Encoding.ASCII.GetBytes(Convert.ToBase64String(new UTF8Encoding(false).GetBytes(Xhtml)));
        envelope[4] = 0xE9;  // 'é' - never part of a Base64 envelope

        Assert.Throws<Base64DecodingException>(() => Base64InputDecoder.Decode(envelope));
    }

    [Fact]
    public void A_decoding_failure_is_permanent_so_the_file_is_not_retried()
    {
        Base64DecodingException failure = Assert.Throws<Base64DecodingException>(
            () => Base64InputDecoder.Decode(Encoding.UTF8.GetBytes(Xhtml)));

        Assert.Equal(ErrorCategory.Permanent, ErrorClassifier.Classify(failure));
    }

    [Fact]
    public void The_failure_message_does_not_quote_the_file_content()
    {
        Base64DecodingException failure = Assert.Throws<Base64DecodingException>(
            () => Base64InputDecoder.Decode(Encoding.UTF8.GetBytes(Xhtml)));

        Assert.DoesNotContain("Progress Note", failure.Message, StringComparison.Ordinal);
    }
}
