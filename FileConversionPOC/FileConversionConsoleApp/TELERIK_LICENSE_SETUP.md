# Setup Instructions for Telerik License

## Option 1: Using Telerik Trial License (Recommended)

### Step 1: Get a Trial License
1. Visit: https://www.telerik.com/try/document-processing
2. Sign in with your Telerik account (or create a new one)
3. Download the license file: `telerik-license.txt`

### Step 2: Place License File
Place the downloaded `telerik-license.txt` in one of these locations:

**Option A: Project Directory (Recommended for this project)**
```
D:\FileConversionPOC\FileConversionConsoleApp\telerik-license.txt
```

**Option B: User AppData Directory (Global)**
```
C:\Users\<YourUsername>\AppData\Roaming\Telerik\telerik-license.txt
```

**Option C: Environment Variable**
Set environment variable:
- Variable name: `TELERIK_LICENSE_PATH`
- Variable value: `C:\path\to\telerik-license.txt`

### Step 3: Build the Project
```bash
dotnet clean
dotnet build
dotnet run
```

## Option 2: Using Environment Variable (Alternative)

1. Get your license content from the license file
2. Set environment variable: `TELERIK_LICENSE` with the license content

## Option 3: Programmatic Activation (If needed)

Add this to the top of `Program.cs`:

```csharp
// Set license programmatically
Telerik.Licensing.LicenseManager.AddLicenseFile("path/to/telerik-license.txt");
```

## Troubleshooting

**Q: Still getting "No Telerik and Kendo UI License file found"?**
- Verify the license file exists in the correct location
- Ensure file is named exactly: `telerik-license.txt`
- Try placing it in the project root directory
- Restart Visual Studio or terminal

**Q: License file format issues?**
- The file should contain XML content starting with `<License>`
- Ensure UTF-8 encoding without BOM

**Q: Still getting compilation errors?**
- Clean NuGet cache: `nuget locals all -clear`
- Delete `bin` and `obj` folders manually
- Run `dotnet restore` explicitly
- Try rebuilding: `dotnet clean && dotnet build`

## Trial License Duration

Telerik trial licenses typically include:
- 30-day free trial from download date
- Full feature access during trial period
- No code limitations

After trial expires, you can:
- Purchase a license
- Request another trial
- Use the Community License (if available for Document Processing)

## Support

- Telerik Support: https://www.telerik.com/support
- Documentation: https://docs.telerik.com/devtools/document-processing/
- Community Forums: https://www.telerik.com/forums/document-processing

---

**Note:** Once you have a valid license file in place, the project will build and run successfully.
