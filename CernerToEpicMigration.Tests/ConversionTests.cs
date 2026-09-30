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
    public void A_cerner_bullet_table_document_exports_with_visible_unicode_bullets()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "bullets.xhtml");
        string output = Path.Combine(workspace.OutputPath, "bullets.rtf");
        TempWorkspace.WriteInput(
            input,
            """<?xml version="1.0" encoding="utf-8"?><html><body><div> <table style="table-layout: fixed; border-collapse: collapse;" valign="top"><tbody><tr><td valign="top"><div style="margin-top: 1em; margin-bottom: 1em; margin: 0px; padding-left: 15px;"><table width="100%"><tbody><tr><td valign="top" style="text-align: right; white-space: nowrap;" width="24"><div><span style="font: 1em serif;">&#8226;</span></div></td><td width="8"> </td><td valign="top"><div>Knee replacement (2004)</div></td></tr></tbody></table></div></td></tr></tbody></table> </div></body></html>""");

        workspace.CreateConverter().Convert(input, output);

        string rtf = File.ReadAllText(output);
        Assert.Contains("Knee replacement (2004)", rtf, StringComparison.Ordinal);
        Assert.Contains("\\u8226?", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u-3913?", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain("\\listtext", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cerner_unordered_list_document_exports_with_visible_unicode_bullets()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "list.xhtml");
        string output = Path.Combine(workspace.OutputPath, "list.rtf");
        TempWorkspace.WriteInput(
            input,
            """<?xml version="1.0" encoding="utf-8"?><html><body><div class="ddemrcontent" id="_c72f" dd:contenttype="PROCEDURES"><ul style="margin: 0px; padding-left: 15px; list-style-type: disc;" xmlns:dd="DynamicDocumentation"><li class="ddemrcontentitem ddremovable" id="_6a33" dd:contenttype="PROCEDURES" dd:entityid="2612261207">Colonoscopy, flexible; with biopsy, single or multiple (04/26/2023)</li><li class="ddemrcontentitem ddremovable" id="_8ac5" dd:contenttype="PROCEDURES" dd:entityid="2612447193">EGD - Esophagogastroduodenoscopy</li></ul></div></body></html>""");

        workspace.CreateConverter().Convert(input, output);

        string rtf = File.ReadAllText(output);
        Assert.Contains("Colonoscopy", rtf, StringComparison.Ordinal);
        Assert.Contains("EGD - Esophagogastroduodenoscopy", rtf, StringComparison.Ordinal);
        Assert.Contains("\\u8226?", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u-3913?", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain("\\listtext", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void A_strikethrough_on_an_ancestor_span_survives_into_the_rtf()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "strike.xhtml");
        string output = Path.Combine(workspace.OutputPath, "strike.rtf");
        TempWorkspace.WriteInput(
            input,
            """<?xml version="1.0" encoding="utf-8"?><html><body><span style="font-size: 16pt;"><span style="color: rgb(243, 156, 18);"><span style="text-decoration: line-through;"><span style="font-weight: bold;"><span style="font-style: italic;"><span style="text-decoration: underline;">Test the sentence </span></span></span></span></span></span></body></html>""");

        workspace.CreateConverter().Convert(input, output);

        // \strike0 is strike OFF - assert the control word is followed by a non-digit.
        Assert.Matches(@"\\strike(?![0-9])", File.ReadAllText(output));
    }

    [Fact]
    public void Empty_named_anchors_do_not_become_invalid_hyperlink_fields()
    {
        using TempWorkspace workspace = new();
        string input = Path.Combine(workspace.InputPath, "anchors.xhtml");
        string output = Path.Combine(workspace.OutputPath, "anchors.rtf");
        TempWorkspace.WriteInput(
            input,
            """<?xml version="1.0" encoding="utf-8"?><html><body><a name="_2ae31cb6-bf8e-4960-beb2-1162a8181340"></a><div>Care Plan</div><a name="_7c0ccacc-f2ae-49dc-8970-96d2823d5d33"></a><div>Problems</div></body></html>""");

        workspace.CreateConverter().Convert(input, output);

        string rtf = File.ReadAllText(output);
        Assert.Contains("Care Plan", rtf, StringComparison.Ordinal);
        Assert.Contains("Problems", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain("HYPERLINK", rtf, StringComparison.Ordinal);
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
