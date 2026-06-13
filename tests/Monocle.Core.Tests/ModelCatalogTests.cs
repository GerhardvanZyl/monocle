using System;
using Monocle.Models.Onnx;
using Monocle.Models.Sidecar;
using Xunit;

namespace Monocle.Core.Tests;

public class ModelCatalogTests
{
    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    [Fact]
    public void OnnxConfigs_AnyDownloadUrlHasAChecksum()
    {
        // The installer trusts a checksum to reject a bad/mismatched file; a URL without one would
        // let unverified weights through. This guards that invariant as the catalog grows.
        foreach (var c in OnnxModelCatalog.Configs)
            if (!string.IsNullOrWhiteSpace(c.DownloadUrl))
                Assert.False(string.IsNullOrWhiteSpace(c.Sha256),
                    $"ONNX model '{c.Id}' has a DownloadUrl but no Sha256.");
    }

    [Fact]
    public void OnnxConfigs_InfoUrlsAreWellFormed()
    {
        foreach (var c in OnnxModelCatalog.Configs)
            if (!string.IsNullOrWhiteSpace(c.InfoUrl))
                Assert.True(IsWebUrl(c.InfoUrl!), $"ONNX model '{c.Id}' has a malformed InfoUrl: {c.InfoUrl}");
    }

    [Fact]
    public void SidecarModels_InfoUrlsAreWellFormed()
    {
        foreach (var m in SidecarModelCatalog.Models)
            if (!string.IsNullOrWhiteSpace(m.InfoUrl))
                Assert.True(IsWebUrl(m.InfoUrl!), $"Sidecar model '{m.Id}' has a malformed InfoUrl: {m.InfoUrl}");
    }

    [Fact]
    public void SidecarModels_AllHaveAHuggingFaceLink()
    {
        foreach (var m in SidecarModelCatalog.Models)
            Assert.True(IsWebUrl(m.InfoUrl ?? ""), $"Sidecar model '{m.Id}' is missing its source link.");
    }
}
