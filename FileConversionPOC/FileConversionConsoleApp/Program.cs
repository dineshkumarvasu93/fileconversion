using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FileConversionConsoleApp;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════╗");
        Console.WriteLine("║   XHTML to RTF Converter - .NET 8      ║");
        Console.WriteLine("║   Telerik Document Processing          ║");
        Console.WriteLine("╚════════════════════════════════════════╝\n");

        // Non-interactive usage:
        // FileConversionConsoleApp <inputFolder> <outputFolder> [extensions] [--recursive]
        // e.g. FileConversionConsoleApp D:\XHTML D:\RTF .xhtml,.html --recursive
        if (args.Length >= 2)
        {
            bool recursive = args.Any(a => a.Equals("--recursive", StringComparison.OrdinalIgnoreCase));
            string? extensionArg = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--"));
            ConvertFolder(args[0], args[1], XhtmlToRtfConverter.ParseExtensions(extensionArg), recursive);
            return;
        }

        DisplayMenu();
    }

    static void DisplayMenu()
    {
        bool running = true;

        while (running)
        {
            Console.WriteLine("\nOptions:");
            Console.WriteLine("1. Convert XHTML String to RTF");
            Console.WriteLine("2. Convert XHTML File to RTF");
            Console.WriteLine("3. Convert XHTML Folder to RTF (batch)");
            Console.WriteLine("4. Create Sample XHTML File");
            Console.WriteLine("5. Show License Setup Instructions");
            Console.WriteLine("6. Exit");
            Console.Write("\nSelect an option (1-6): ");

            string choice = Console.ReadLine() ?? "";

            switch (choice)
            {
                case "1":
                    ConvertXhtmlStringToRtf();
                    break;
                case "2":
                    ConvertXhtmlFileToRtf();
                    break;
                case "3":
                    ConvertXhtmlFolderToRtf();
                    break;
                case "4":
                    CreateSampleXhtmlFile();
                    break;
                case "5":
                    ShowLicenseInstructions();
                    break;
                case "6":
                    running = false;
                    Console.WriteLine("\nThank you for using XHTML to RTF Converter!");
                    break;
                default:
                    Console.WriteLine("✗ Invalid option. Please select 1-6.");
                    break;
            }
        }
    }

    static void ConvertXhtmlStringToRtf()
    {
        Console.WriteLine("\n--- Converting XHTML String to RTF ---\n");

        string xhtmlContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<html xmlns=""http://www.w3.org/1999/xhtml"">
<head>
    <title>Sample Document</title>
</head>
<body>
    <h1>Welcome to RTF Conversion</h1>
    <p>This is a <strong>sample XHTML</strong> document being converted to <em>RTF format</em>.</p>
    <ul>
        <li>Feature 1: Easy conversion</li>
        <li>Feature 2: Professional quality</li>
        <li>Feature 3: Telerik powered</li>
    </ul>
    <p>This demonstrates the Telerik Document Processing Library capabilities.</p>
</body>
</html>";

        try
        {
            string rtfContent = XhtmlToRtfConverter.ConvertXhtmlStringToRtf(xhtmlContent);
            string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output_string.rtf");
            File.WriteAllText(outputPath, rtfContent, Encoding.UTF8);

            Console.WriteLine($"✓ XHTML string converted successfully!");
            Console.WriteLine($"✓ Output saved to: {outputPath}");
            Console.WriteLine($"✓ RTF Content size: {rtfContent.Length} characters");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
            Console.WriteLine("\nNote: Ensure Telerik license is configured.");
            Console.WriteLine("See 'Option 4' for setup instructions.");
        }
    }

    static void ConvertXhtmlFileToRtf()
    {
        Console.WriteLine("\n--- Converting XHTML File to RTF ---\n");

        Console.Write("Enter XHTML file path (or press Enter for sample): ");
        string? inputPath = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            inputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sample.xhtml");
            if (!File.Exists(inputPath))
            {
                CreateSampleXhtmlFile();
            }
        }

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"✗ File not found: {inputPath}");
            return;
        }

        string outputPath = Path.ChangeExtension(inputPath, ".rtf");
        Console.Write($"Output file path [{outputPath}]: ");
        string? customOutput = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(customOutput))
        {
            outputPath = customOutput;
        }

        try
        {
            XhtmlToRtfConverter.ConvertXhtmlFileToRtf(inputPath, outputPath);
            Console.WriteLine($"✓ Conversion complete!");
            Console.WriteLine($"✓ Input:  {inputPath}");
            Console.WriteLine($"✓ Output: {outputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
            Console.WriteLine("\nNote: Ensure Telerik license is configured.");
            Console.WriteLine("See 'Option 4' for setup instructions.");
        }
    }

    static void ConvertXhtmlFolderToRtf()
    {
        Console.WriteLine("\n--- Batch Converting XHTML Folder to RTF ---\n");

        Console.Write("Enter input folder (containing XHTML files): ");
        string inputFolder = (Console.ReadLine() ?? "").Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(inputFolder))
        {
            Console.WriteLine("✗ Input folder is required.");
            return;
        }

        Console.Write("Enter target folder (for RTF output): ");
        string outputFolder = (Console.ReadLine() ?? "").Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            Console.WriteLine("✗ Target folder is required.");
            return;
        }

        string[]? extensions = SelectExtensions(inputFolder);
        if (extensions == null)
            return;

        Console.Write("Include sub-folders? (y/N): ");
        bool recursive = (Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);

        ConvertFolder(inputFolder, outputFolder, extensions, recursive);
    }

    /// <summary>
    /// Lets the user pick which extensions to convert from a numbered list.
    /// Returns null when the selection is invalid or cancelled.
    /// </summary>
    static string[]? SelectExtensions(string inputFolder)
    {
        string[] supported = XhtmlToRtfConverter.SupportedExtensions;

        Console.WriteLine("\nWhich file extension(s) should be converted?");
        for (int i = 0; i < supported.Length; i++)
        {
            Console.WriteLine($"  {i + 1}. {supported[i]}{DescribeMatches(inputFolder, supported[i])}");
        }
        Console.WriteLine($"  {supported.Length + 1}. All supported ({string.Join(", ", supported)})");
        Console.WriteLine($"  0. Cancel");
        Console.Write($"\nSelect (1-{supported.Length + 1}, comma separated for multiple) [{supported.Length + 1}]: ");

        string input = (Console.ReadLine() ?? "").Trim();
        if (string.IsNullOrEmpty(input))
            return supported;

        string[] tokens = input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var selected = new List<string>();

        foreach (string token in tokens)
        {
            if (!int.TryParse(token, out int choice) || choice < 0 || choice > supported.Length + 1)
            {
                Console.WriteLine($"✗ Invalid selection: {token}");
                return null;
            }

            if (choice == 0)
            {
                Console.WriteLine("Cancelled.");
                return null;
            }

            if (choice == supported.Length + 1)
                return supported;

            selected.Add(supported[choice - 1]);
        }

        return selected.Distinct().ToArray();
    }

    static string DescribeMatches(string folder, string extension)
    {
        try
        {
            if (!Directory.Exists(folder))
                return "";

            int count = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
                .Count(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase));
            return count > 0 ? $"  ({count} file(s) found)" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    static void ConvertFolder(string inputFolder, string outputFolder, string[] extensions, bool recursive)
    {
        try
        {
            Console.WriteLine($"\nInput:  {Path.GetFullPath(inputFolder)}");
            Console.WriteLine($"Target: {Path.GetFullPath(outputFolder)}");
            Console.WriteLine($"Filter: {string.Join(", ", extensions)}\n");

            var result = XhtmlToRtfConverter.ConvertFolder(inputFolder, outputFolder, extensions, recursive);

            Console.WriteLine($"\n✓ Converted: {result.Converted} file(s)");
            if (result.Failures.Count > 0)
            {
                Console.WriteLine($"✗ Failed: {result.Failures.Count} file(s)");
                foreach (var failure in result.Failures)
                {
                    Console.WriteLine($"  - {failure.File}: {failure.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
            Console.WriteLine("\nNote: Ensure Telerik license is configured.");
            Console.WriteLine("See 'Option 5' for setup instructions.");
        }
    }

    static void CreateSampleXhtmlFile()
    {
        string samplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sample.xhtml");

        string sampleXhtml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<html xmlns=""http://www.w3.org/1999/xhtml"">
<head>
    <title>Document Conversion Sample</title>
</head>
<body>
    <h1>Document Processing with Telerik</h1>
    
    <h2>Introduction</h2>
    <p>This document demonstrates the conversion from XHTML to RTF format using the Telerik Document Processing Library.</p>
    
    <h2>Features</h2>
    <ul>
        <li><strong>High-quality</strong> document conversion</li>
        <li><strong>Format Support</strong> - XHTML, RTF, DOCX, and more</li>
        <li><strong>Easy Integration</strong> - Simple API for developers</li>
    </ul>
    
    <h2>Text Formatting</h2>
    <p>This paragraph contains <strong>bold text</strong>, <em>italic text</em>, and <u>underlined text</u>.</p>
    
    <h2>Code Section</h2>
    <pre><code>HtmlFormatProvider htmlProvider = new HtmlFormatProvider();
RadFlowDocument document = htmlProvider.Import(xhtmlContent);
RtfFormatProvider rtfProvider = new RtfFormatProvider();
string rtfContent = rtfProvider.Export(document);</code></pre>
    
    <p>For more information, visit <a href=""https://www.telerik.com/"">Telerik.com</a></p>
</body>
</html>";

        try
        {
            File.WriteAllText(samplePath, sampleXhtml, Encoding.UTF8);
            Console.WriteLine($"✓ Sample XHTML file created: {samplePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error creating sample file: {ex.Message}");
        }
    }

    static void ShowLicenseInstructions()
    {
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           TELERIK LICENSE SETUP INSTRUCTIONS                ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine(@"
1. GET A TRIAL LICENSE
   • Visit: https://www.telerik.com/try/document-processing
   • Sign in with Telerik account (create if needed)
   • Download: telerik-license.txt

2. PLACE THE LICENSE FILE
   Place the file in ONE of these locations:

   Option A (Project Directory - Recommended):
   📁 D:\FileConversionPOC\FileConversionConsoleApp\telerik-license.txt

   Option B (User AppData - Global):
   📁 C:\Users\<YourUsername>\AppData\Roaming\Telerik\telerik-license.txt

   Option C (Environment Variable):
   • Set TELERIK_LICENSE_PATH variable with path to license file
   • Or set TELERIK_LICENSE with license content

3. BUILD AND RUN
   • dotnet clean
   • dotnet build
   • dotnet run

4. VERIFY
   If successful, you should see:
   ✓ Build succeeds without compilation errors
   ✓ Application runs with conversion options available

NEED HELP?
• Telerik Docs: https://docs.telerik.com/devtools/document-processing/
• Support: https://www.telerik.com/support
• See file: TELERIK_LICENSE_SETUP.md for detailed instructions
");
    }
}
