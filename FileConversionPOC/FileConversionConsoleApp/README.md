# XHTML to RTF Converter - Console Application

A .NET Core 8 console application that converts XHTML format documents to RTF (Rich Text Format) using the Telerik Document Processing Library.

## Prerequisites

- .NET Core 8 SDK (or later)
- Telerik Document Processing Library (automatically installed via NuGet)

## Project Setup

### 1. Build the Project

```bash
dotnet build
```

This command will:
- Restore NuGet packages (including Telerik libraries)
- Compile the project
- Generate the executable

### 2. Run the Application

```bash
dotnet run
```

## How It Works

The application demonstrates two conversion methods:

### Example 1: Convert XHTML String to RTF
- Takes an XHTML string directly
- Parses it using `HtmlFormatProvider`
- Converts to RTF format using `RtfFormatProvider`
- Saves the output as `output_string.rtf`

### Example 2: Convert XHTML File to RTF
- Creates a sample XHTML file (`sample.xhtml`)
- Reads and parses the XHTML content
- Converts to RTF format
- Saves the output as `output_file.rtf`

## Output Files

After running the application, you'll find:
- `output_string.rtf` - RTF converted from XHTML string
- `output_file.rtf` - RTF converted from XHTML file
- `sample.xhtml` - Sample XHTML file used for demonstration

## Code Structure

### Program.cs
- **Main()** - Entry point that runs both conversion examples
- **ConvertXhtmlStringToRtf()** - Converts XHTML string content to RTF
- **ConvertXhtmlFileToRtf()** - Converts XHTML file to RTF
- **CreateSampleXhtmlFile()** - Creates a sample XHTML file for testing

## Key Classes Used

- **HtmlFormatProvider** - Imports XHTML content
- **RtfFormatProvider** - Exports to RTF format
- **RadFlowDocument** - Represents the document being processed

## Supported Features

✓ HTML/XHTML import
✓ Text formatting (bold, italic, underline)
✓ Headings and paragraphs
✓ Lists (ordered and unordered)
✓ Hyperlinks
✓ Code blocks
✓ RTF export

## Extending the Application

To add more features:

1. **Convert from file path:**
   ```csharp
   using (FileStream stream = File.OpenRead("input.xhtml"))
   {
       var document = htmlProvider.Import(stream);
   }
   ```

2. **Export to different formats:**
   ```csharp
   // Export to DOCX
   DocxFormatProvider docxProvider = new DocxFormatProvider();
   docxProvider.Export(document, "output.docx");
   ```

3. **Custom document processing:**
   - Modify content before export
   - Add headers/footers
   - Apply styles and formatting

## NuGet Packages

- **Telerik.Documents.Core** - Core document processing framework
- **Telerik.Documents.Flow** - Flow document support (Word, RTF, HTML)

## Troubleshooting

### Package Not Found Error
If you encounter Telerik package errors, ensure:
1. You have proper NuGet sources configured
2. Run `dotnet restore` explicitly
3. Check your .csproj file for correct package versions

### Conversion Errors
- Ensure XHTML is well-formed XML
- Check file encoding (UTF-8 recommended)
- Verify file paths and permissions

## License

This project uses Telerik Document Processing Library. Ensure you have proper licensing for production use.

## References

- [Telerik Document Processing Documentation](https://www.telerik.com/products/document-processing)
- [.NET Documentation](https://docs.microsoft.com/en-us/dotnet/)
