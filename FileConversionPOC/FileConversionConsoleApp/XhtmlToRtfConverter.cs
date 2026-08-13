using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Telerik.Windows.Documents.Flow.FormatProviders.Html;
using Telerik.Windows.Documents.Flow.FormatProviders.Rtf;
using Telerik.Windows.Documents.Flow.Model;

namespace FileConversionConsoleApp
{
    /// <summary>
    /// Wrapper class for converting XHTML to RTF
    /// This can be used with Telerik Document Processing Library
    /// </summary>
    public class XhtmlToRtfConverter
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Extensions the HTML based flow import supports.
        /// </summary>
        public static readonly string[] SupportedExtensions = { ".xhtml", ".html", ".htm" };

        /// <summary>
        /// Normalizes a user supplied extension filter (e.g. "xhtml", ".XHTML", "xhtml,html")
        /// into a distinct list of lower-case extensions prefixed with a dot.
        /// </summary>
        public static string[] ParseExtensions(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return SupportedExtensions;

            return input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().TrimStart('*'))
                .Select(e => e.StartsWith(".") ? e : "." + e)
                .Select(e => e.ToLowerInvariant())
                .Distinct()
                .ToArray();
        }

        /// <summary>
        /// Converts XHTML content to RTF format
        /// Note: Requires Telerik Document Processing Library with valid license
        /// </summary>
        /// <param name="xhtmlContent">XHTML content as string</param>
        /// <returns>RTF content as string</returns>
        public static string ConvertXhtmlStringToRtf(string xhtmlContent)
        {
            HtmlFormatProvider htmlProvider = new HtmlFormatProvider();
            RadFlowDocument document = htmlProvider.Import(xhtmlContent, Timeout);

            RtfFormatProvider rtfProvider = new RtfFormatProvider();
            return rtfProvider.Export(document, Timeout);
        }

        /// <summary>
        /// Converts XHTML file to RTF file
        /// </summary>
        /// <param name="xhtmlFilePath">Path to input XHTML file</param>
        /// <param name="rtfOutputPath">Path for output RTF file</param>
        public static void ConvertXhtmlFileToRtf(string xhtmlFilePath, string rtfOutputPath)
        {
            if (!File.Exists(xhtmlFilePath))
                throw new FileNotFoundException($"XHTML file not found: {xhtmlFilePath}");

            string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(rtfOutputPath));
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            string xhtmlContent = File.ReadAllText(xhtmlFilePath, Encoding.UTF8);
            string rtfContent = ConvertXhtmlStringToRtf(xhtmlContent);
            File.WriteAllText(rtfOutputPath, rtfContent, Encoding.UTF8);
        }

        /// <summary>
        /// Converts every XHTML/HTML file in a source folder to RTF into the target folder.
        /// The target folder is created when it does not exist.
        /// </summary>
        /// <param name="sourceFolder">Folder containing the XHTML files</param>
        /// <param name="targetFolder">Folder where the RTF files are written</param>
        /// <param name="extensions">Extensions to pick up, e.g. [".xhtml"]. Defaults to all supported extensions</param>
        /// <param name="recursive">When true, sub-folders are processed and their structure is mirrored</param>
        /// <returns>Number of successfully converted files and the failures per file</returns>
        public static (int Converted, IReadOnlyList<(string File, string Error)> Failures) ConvertFolder(
            string sourceFolder, string targetFolder, string[]? extensions = null, bool recursive = false)
        {
            if (!Directory.Exists(sourceFolder))
                throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

            string[] filter = extensions == null || extensions.Length == 0 ? SupportedExtensions : extensions;
            string[] unsupported = filter.Except(SupportedExtensions, StringComparer.OrdinalIgnoreCase).ToArray();
            if (unsupported.Length > 0)
            {
                throw new NotSupportedException(
                    $"Unsupported extension(s): {string.Join(", ", unsupported)}. " +
                    $"HTML to RTF conversion supports only: {string.Join(", ", SupportedExtensions)}");
            }

            Directory.CreateDirectory(targetFolder);

            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] inputFiles = Directory.GetFiles(sourceFolder, "*.*", searchOption)
                .Where(f => filter.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToArray();

            int converted = 0;
            List<(string File, string Error)> failures = new List<(string, string)>();

            foreach (string inputFile in inputFiles)
            {
                string relativePath = Path.GetRelativePath(sourceFolder, inputFile);
                string outputPath = Path.Combine(targetFolder, Path.ChangeExtension(relativePath, ".rtf"));

                try
                {
                    ConvertXhtmlFileToRtf(inputFile, outputPath);
                    converted++;
                    Console.WriteLine($"✓ {relativePath} -> {Path.GetRelativePath(targetFolder, outputPath)}");
                }
                catch (Exception ex)
                {
                    failures.Add((relativePath, ex.Message));
                    Console.WriteLine($"✗ {relativePath}: {ex.Message}");
                }
            }

            if (inputFiles.Length == 0)
                Console.WriteLine($"No {string.Join("/", filter)} files found in: {sourceFolder}");

            return (converted, failures);
        }
    }
}
