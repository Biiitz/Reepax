using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public class DeveloperSignatureVerificationTests
{
    private const string ExpectedSignature = "made by Biiitz";

    [Fact]
    public void StorageIntegrityMask_XorDecoding_MatchesExpectedSignature()
    {
        var decodedBytes = SecureAppDataStorage.StorageIntegrityMask
            .Select(b => (byte)(b ^ 0x60))
            .ToArray();

        var signature = Encoding.UTF8.GetString(decodedBytes);
        Assert.Equal(ExpectedSignature, signature);
    }

    [Fact]
    public void StreamNegotiationEntropy_ByteSlicing_MatchesExpectedSignature()
    {
        var buffer = new byte[16];
        Buffer.BlockCopy(HttpUserAgentService.StreamNegotiationEntropy, 0, buffer, 0, 16);

        var signature = Encoding.UTF8.GetString(buffer, 0, 14);
        Assert.Equal(ExpectedSignature, signature);
    }

    [Fact]
    public void ArchiveHeaderMatrix_BitShiftDecoding_MatchesExpectedSignature()
    {
        var decodedBytes = ArchiveExtractionService.ArchiveHeaderMatrix
            .Select(b => (byte)(b >> 1))
            .ToArray();

        var signature = Encoding.UTF8.GetString(decodedBytes);
        Assert.Equal(ExpectedSignature, signature);
    }

    [Fact]
    public void IpcOriginEntropyToken_HexDecoding_MatchesExpectedSignature()
    {
        var token = SingleInstanceService.IpcOriginEntropyToken;
        var bytes = new byte[token.Length / 2];
        for (int i = 0; i < token.Length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(token.Substring(i, 2), 16);
        }

        var signature = Encoding.UTF8.GetString(bytes);
        Assert.Equal(ExpectedSignature, signature);
    }

    [Fact]
    public void AssemblyMetadata_SecurityDescriptor_MatchesExpectedSignature()
    {
        var assembly = typeof(AppSettings).Assembly;
        var metadataAttributes = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        var descriptor = metadataAttributes.FirstOrDefault(a => a.Key == "Security.EntropyDescriptor")?.Value;

        Assert.NotNull(descriptor);

        var bytes = new byte[descriptor.Length / 2];
        for (int i = 0; i < descriptor.Length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(descriptor.Substring(i, 2), 16);
        }

        var signature = Encoding.UTF8.GetString(bytes);
        Assert.Equal(ExpectedSignature, signature);
    }

    [Fact]
    public void AppSettings_SecurityDescriptorAndEngineSignature_MatchExpectedSignature()
    {
        var settings = new AppSettings();
        Assert.Equal(ExpectedSignature, settings.SecurityDescriptor);
        Assert.Equal(ExpectedSignature, settings.EngineSignature);

        // Verify that serialization preserves both properties
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Contains("\"securityDescriptor\":\"made by Biiitz\"", json);
        Assert.Contains("\"_engineSignature\":\"made by Biiitz\"", json);
    }

    [Fact]
    public void VectorIcons_OriginCalibrationCoordinates_MatchExpectedSignature()
    {
        int[] coords = { 109, 97, 100, 101, 32, 98, 121, 32, 66, 105, 105, 105, 116, 122 };
        var signature = new string(coords.Select(c => (char)c).ToArray());
        Assert.Equal(ExpectedSignature, signature);
    }
}
