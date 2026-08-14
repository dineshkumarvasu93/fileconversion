using System.Xml;
using CernerToEpicMigration.Models;
using CernerToEpicMigration.Processing;
using Xunit;

namespace CernerToEpicMigration.Tests;

/// <summary>
/// The classification decides whether a document is retried, failed, or whether the whole
/// migration stops - so each category has a worked example.
/// </summary>
public class ErrorClassifierTests
{
    [Fact]
    public void A_locked_file_is_transient_and_will_be_retried()
    {
        IOException locked = new("The process cannot access the file because it is being used by another process.");

        Assert.Equal(ErrorCategory.Transient, ErrorClassifier.Classify(locked));
    }

    [Fact]
    public void A_timeout_is_transient()
    {
        Assert.Equal(ErrorCategory.Transient, ErrorClassifier.Classify(new TimeoutException()));
    }

    [Theory]
    [InlineData(0x27)] // ERROR_HANDLE_DISK_FULL
    [InlineData(0x70)] // ERROR_DISK_FULL
    public void A_full_disk_is_fatal_and_stops_the_run(int win32Code)
    {
        IOException diskFull = new("There is not enough space on the disk.")
        {
            HResult = unchecked((int)0x80070000) | win32Code
        };

        Assert.Equal(ErrorCategory.Fatal, ErrorClassifier.Classify(diskFull));
    }

    [Fact]
    public void Denied_permissions_are_fatal()
    {
        Assert.Equal(ErrorCategory.Fatal, ErrorClassifier.Classify(new UnauthorizedAccessException()));
    }

    [Fact]
    public void A_licensing_failure_is_fatal_however_it_is_thrown()
    {
        Exception licensing = new InvalidOperationException("Your Telerik license has expired.");

        Assert.Equal(ErrorCategory.Fatal, ErrorClassifier.Classify(licensing));
    }

    [Fact]
    public void A_licensing_failure_nested_in_an_inner_exception_is_still_fatal()
    {
        Exception licensing = new InvalidOperationException(
            "Conversion failed", new Exception("No Telerik and Kendo UI License file found"));

        Assert.Equal(ErrorCategory.Fatal, ErrorClassifier.Classify(licensing));
    }

    [Fact]
    public void Malformed_markup_is_permanent_and_is_not_retried()
    {
        Assert.Equal(ErrorCategory.Permanent, ErrorClassifier.Classify(new XmlException("Invalid XHTML at line 42")));
    }

    [Fact]
    public void An_unrecognised_error_is_treated_as_a_bad_document_rather_than_retried()
    {
        Assert.Equal(ErrorCategory.Permanent, ErrorClassifier.Classify(new InvalidOperationException("odd")));
    }
}
