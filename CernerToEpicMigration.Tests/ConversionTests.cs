using System.Text;
using CernerToEpicMigration.Processing;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// Exercises the real Telerik conversion. These tests also prove the licence is picked up:
/// a missing licence surfaces here rather than in production.
/// </summary>
public class ConversionTests
{
    static ConversionTests() => XhtmlDocumentReader.RegisterLegacyCodePages();

    [Fact]
    public void An_xhtml_document_is_converted_to_an_rtf_file()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "note.xhtml");
        string output = Path.Combine(workspace.OutputPath, "note.rtf");
        TempWorkspace.WriteInput(input, TempWorkspace.SampleXhtml);

        ConversionOutcome outcome = workspace.CreateConverter().Convert(input, output);

        Assert.True(File.Exists(output));
        Assert.True(outcome.OutputBytes > 0);
        Assert.Equal(new FileInfo(input).Length, outcome.InputBytes);
        Assert.Equal(new FileInfo(output).Length, outcome.OutputBytes);
        Assert.Contains("Progress Note", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public void The_rtf_starts_with_the_rtf_signature_and_carries_no_byte_order_mark()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "note.xhtml");
        string output = Path.Combine(workspace.OutputPath, "note.rtf");
        TempWorkspace.WriteInput(input, TempWorkspace.SampleXhtml);

        workspace.CreateConverter().Convert(input, output);

        byte[] bytes = File.ReadAllBytes(output);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "RTF must not start with a UTF-8 BOM.");
        Assert.StartsWith("{\\rtf", Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
    }

    [Fact]
    public void Accented_clinical_text_survives_the_conversion_as_rtf_escapes()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "accents.xhtml");
        string output = Path.Combine(workspace.OutputPath, "accents.rtf");
        TempWorkspace.WriteInput(
            input,
            """<?xml version="1.0" encoding="utf-8"?><html><body><p>Dr Müller — 37°C, 5 µg/mL</p></body></html>""");

        workspace.CreateConverter().Convert(input, output);

        byte[] bytes = File.ReadAllBytes(output);
        Assert.All(bytes, b => Assert.True(b < 0x80, "RTF output should be 7-bit; non-ASCII must be escaped."));

        string rtf = Encoding.ASCII.GetString(bytes);
        Assert.Contains("\\u252", rtf, StringComparison.Ordinal);  // u umlaut
        Assert.Contains("\\u176", rtf, StringComparison.Ordinal);  // degree sign
        Assert.Contains("\\u181", rtf, StringComparison.Ordinal);  // micro sign
    }

    [Fact]
    public void A_document_declaring_windows_1252_is_not_corrupted()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "legacy.xhtml");
        string output = Path.Combine(workspace.OutputPath, "legacy.rtf");
        TempWorkspace.WriteInput(
            input,
            """<?xml version="1.0" encoding="windows-1252"?><html><body><p>Café 37°C</p></body></html>""",
            Encoding.GetEncoding("windows-1252"));

        workspace.CreateConverter().Convert(input, output);

        string rtf = File.ReadAllText(output);
        Assert.Contains("\\u233", rtf, StringComparison.Ordinal);  // e acute, not a replacement character
        Assert.DoesNotContain("\\u65533", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_input_file_is_reported_as_a_file_not_found()
    {
        using TempWorkspace workspace = new();

        Assert.Throws<FileNotFoundException>(() => workspace.CreateConverter().Convert(
            Path.Combine(workspace.InputPath, "absent.xhtml"),
            Path.Combine(workspace.OutputPath, "absent.rtf")));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "note.xhtml");
        string output = Path.Combine(workspace.OutputPath, "note.rtf");
        TempWorkspace.WriteInput(input, TempWorkspace.SampleXhtml);

        workspace.CreateConverter().Convert(input, output);

        Assert.Empty(Directory.GetFiles(workspace.OutputPath, "*.tmp"));
    }

    [Fact]
    public void A_document_that_is_not_base64_encoded_fails_before_the_converter_writes_anything()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "raw.xhtml");
        string output = Path.Combine(workspace.OutputPath, "raw.rtf");

        // Unwrapped markup: '<' is not a Base64 character, so the envelope cannot be read.
        File.WriteAllText(input, TempWorkspace.SampleXhtml, Encoding.UTF8);

        Assert.Throws<Base64DecodingException>(() => workspace.CreateConverter().Convert(input, output));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(workspace.OutputPath, "*.tmp"));
    }

    [Fact]
    public void The_rtf_is_written_as_a_base64_envelope_when_the_feature_flag_is_on()
    {
        using TempWorkspace workspace = new();
        workspace.Config.Processing.EncodeRtfOutputAsBase64 = true;

        string input = Path.Combine(workspace.InputPath, "note.xhtml");
        string output = Path.Combine(workspace.OutputPath, "note.rtf");
        TempWorkspace.WriteInput(input, TempWorkspace.SampleXhtml);

        ConversionOutcome outcome = workspace.CreateConverter().Convert(input, output);

        byte[] written = File.ReadAllBytes(output);
        Assert.Equal(written.Length, outcome.OutputBytes);

        // What lands on disk is the envelope; the RTF is what comes out of it.
        string rtf = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(written)));
        Assert.StartsWith("{\\rtf", rtf, StringComparison.Ordinal);
        Assert.Contains("Progress Note", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rtf_is_written_as_plain_rtf_when_the_feature_flag_is_off()
    {
        using TempWorkspace workspace = new();
        Assert.False(workspace.Config.Processing.EncodeRtfOutputAsBase64);

        string input = Path.Combine(workspace.InputPath, "note.xhtml");
        string output = Path.Combine(workspace.OutputPath, "note.rtf");
        TempWorkspace.WriteInput(input, TempWorkspace.SampleXhtml);

        workspace.CreateConverter().Convert(input, output);

        Assert.StartsWith("{\\rtf", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public void The_reported_input_size_is_the_size_of_the_file_on_disk()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "note.xhtml");
        string output = Path.Combine(workspace.OutputPath, "note.rtf");
        TempWorkspace.WriteInput(input, TempWorkspace.SampleXhtml);

        ConversionOutcome outcome = workspace.CreateConverter().Convert(input, output);

        // Not the decoded payload: the reports and the disk-space estimate talk about
        // the files an operator can see in the input folder.
        Assert.Equal(new FileInfo(input).Length, outcome.InputBytes);
        Assert.True(outcome.InputBytes > TempWorkspace.SampleXhtml.Length);
    }
}
