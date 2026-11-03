using System.IO;
using Reepax.Services.Storage;

namespace Reepax.Tests;

public class DiagnosticPrivacyTests
{
    internal const string SensitiveMessage = "Read 'Z:\\Users\\TestProfileSecret\\part 01.bin' failed: code=5; " +
        "request 'https://TestAccountSecret:TestCredentialSecret@files.example.test:8443/TestResourceSecret/archive.bin?token=TestQuerySecret#TestFragmentSecret' failed: status=403; retry=2";

    internal static Exception CreateSensitiveException()
    {
        var inner = new IOException(SensitiveMessage);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.SetRemoteStackTrace(inner,
            "   at Example.Component.Read() in Z:\\Users\\TestStackSecret\\Component.cs:line 42");
        return new InvalidOperationException(SensitiveMessage, inner);
    }

    internal static void AssertSensitiveDataRemoved(string text)
    {
        foreach (string marker in new[] { "TestProfileSecret", "TestAccountSecret", "TestCredentialSecret", "TestResourceSecret", "TestQuerySecret", "TestFragmentSecret", "TestStackSecret" })
            Assert.DoesNotContain(marker, text);
        Assert.Contains("https files.example.test:8443", text);
        Assert.Contains("status=403", text);
        Assert.Contains("code=5", text);
        Assert.Contains("retry=2", text);
    }

    [Theory]
    [InlineData("Request https://files.example.test/path?token=prefix'TestApostropheSecret", "TestApostropheSecret")]
    [InlineData("Request https://files.example.test/path?token=prefix\"TestQuoteSecret", "TestQuoteSecret")]
    [InlineData("Request 'https://files.example.test/part 01/TestSpaceSecret?x=1' failed: code=403", "TestSpaceSecret")]
    [InlineData("Request //files.example.test/TestRelativeSecret/archive.bin", "TestRelativeSecret")]
    [InlineData("Read 'Z:\\Example's Folder\\TestApostropheSecret\\part 01.bin' failed: code=5", "TestApostropheSecret")]
    [InlineData("Read Z:\\Example's Folder\\TestUnquotedSecret\\part 01.bin", "TestUnquotedSecret")]
    [InlineData("Read Z:/Example/TestForwardSlashSecret/part.bin", "TestForwardSlashSecret")]
    [InlineData("Read \\\\?\\Z:\\Example\\TestExtendedSecret\\part.bin", "TestExtendedSecret")]
    [InlineData("Read \\\\?\\UNC\\server.example.test\\share\\TestNetworkSecret\\part.bin", "TestNetworkSecret")]
    [InlineData("Read /var/tmp/TestUnixSecret/part.bin", "TestUnixSecret")]
    [InlineData("Request blob:https://files.example.test/TestBlobSecret", "TestBlobSecret")]
    [InlineData("Request data:application/octet-stream;base64,TestDataSecret", "TestDataSecret")]
    public void Sanitize_HandlesEncodedAndAmbiguousLocationsWithoutExposingOriginalValues(string text, string sensitiveValue)
    {
        string safe = DiagnosticPrivacy.Sanitize(text);
        Assert.DoesNotContain(sensitiveValue, safe);
        Assert.Equal(safe, DiagnosticPrivacy.Sanitize(safe));
    }

    [Fact]
    public void Sanitize_RetainsServerAndStatusWithoutCredentialsOrResourceIdentifiers()
    {
        const string message = "Request 'https://TestAccount:TestPassword@files.example.test:8443/TestResource?token=TestQuery#TestFragment' failed: status=403";
        string safe = DiagnosticPrivacy.Sanitize(message);

        Assert.Contains("https files.example.test:8443", safe);
        Assert.Contains("status=403", safe);
        foreach (string value in new[] { "TestAccount", "TestPassword", "TestResource", "TestQuery", "TestFragment" })
            Assert.DoesNotContain(value, safe);
        Assert.Equal(safe, DiagnosticPrivacy.Sanitize(safe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[ERROR] Transfer failed status=403; retry=2")]
    [InlineData("Progress 3/10, 1.5 MB/s")]
    [InlineData("   at Example.Component.Read() :line 42")]
    [InlineData("Reepax/Services/Storage/AppLogger.cs")]
    public void Sanitize_PreservesNonSensitiveDiagnosticText(string text)
    {
        Assert.Equal(text, DiagnosticPrivacy.Sanitize(text));
    }
}
