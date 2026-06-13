using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Monocle.Models.Onnx;
using Xunit;

namespace Monocle.Core.Tests;

public class OnnxModelInstallerTests
{
    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Fact]
    public async Task MatchingChecksum_PlacesFileAtomically()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(dir, "models", "model.onnx");
            var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            using var src = new MemoryStream(bytes);
            await OnnxModelInstaller.FetchAndPlaceAsync(src, bytes.Length, Sha256Hex(bytes), target);

            Assert.True(File.Exists(target));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".download"));   // temp cleaned up
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ChecksumMismatch_Throws_AndLeavesNoFile()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(dir, "model.onnx");
            var bytes = new byte[] { 9, 9, 9, 9 };

            using var src = new MemoryStream(bytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OnnxModelInstaller.FetchAndPlaceAsync(src, bytes.Length, Sha256Hex(new byte[] { 0 }), target));

            Assert.False(File.Exists(target));                 // no partial/garbage model
            Assert.False(File.Exists(target + ".download"));   // temp cleaned up
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task NoChecksum_StillPlacesFile()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(dir, "model.onnx");
            var bytes = new byte[] { 4, 2 };

            using var src = new MemoryStream(bytes);
            await OnnxModelInstaller.FetchAndPlaceAsync(src, bytes.Length, expectedSha256: null, target);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
