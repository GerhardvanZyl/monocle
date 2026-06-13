using System;
using System.Diagnostics;
using Monocle.App.Diagnostics;

namespace Monocle.App.Services;

/// <summary>Opens an external http(s) link in the user's default browser. Scheme-guarded so a model
/// descriptor can't be coaxed into launching an arbitrary local handler.</summary>
public static class UrlLauncher
{
    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Warn($"Refusing to open non-http(s) URL: {url}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't open URL {uri.AbsoluteUri}", ex);
        }
    }
}
