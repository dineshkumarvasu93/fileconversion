using System.Security;
using System.Xml;
using CernerToEpicMigration.Models;

namespace CernerToEpicMigration.Processing;

/// <summary>
/// Maps an exception to one of the three error categories in design document
/// section 10.1, which decides whether a file is retried, failed, or whether the
/// whole run stops.
/// </summary>
public static class ErrorClassifier
{
    // Win32 error codes surfaced through IOException.HResult.
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;

    public static ErrorCategory Classify(Exception exception)
    {
        // A licensing problem does not get better by retrying the next file.
        if (LooksLikeLicenseFailure(exception))
            return ErrorCategory.Fatal;

        return exception switch
        {
            UnauthorizedAccessException => ErrorCategory.Fatal,
            SecurityException => ErrorCategory.Fatal,
            IOException io when IsDiskFull(io) => ErrorCategory.Fatal,
            DriveNotFoundException => ErrorCategory.Fatal,
            PathTooLongException => ErrorCategory.Permanent,
            DirectoryNotFoundException => ErrorCategory.Permanent,
            FileNotFoundException => ErrorCategory.Permanent,

            // File locked by the extract process, transient I/O, or memory pressure.
            IOException => ErrorCategory.Transient,
            TimeoutException => ErrorCategory.Transient,
            OutOfMemoryException => ErrorCategory.Transient,
            OperationCanceledException => ErrorCategory.Transient,

            // A file that is not a decodable Base64 envelope will not decode next time either.
            Base64DecodingException => ErrorCategory.Permanent,

            // Malformed or unsupported document content.
            XmlException => ErrorCategory.Permanent,
            FormatException => ErrorCategory.Permanent,
            NotSupportedException => ErrorCategory.Permanent,
            ArgumentException => ErrorCategory.Permanent,

            // Anything the Telerik import/export throws that we do not recognise is
            // treated as a bad document rather than retried against the whole batch.
            _ => ErrorCategory.Permanent
        };
    }

    private static bool IsDiskFull(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        return code is ErrorDiskFull or ErrorHandleDiskFull;
    }

    private static bool LooksLikeLicenseFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("license", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
