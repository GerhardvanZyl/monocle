using System.IO;
using System.Text.Json;
using Monocle.App.Services;
using Xunit;

namespace Monocle.Core.Tests;

public class CullLauncherTests
{
    [Fact]
    public void McpServerExe_points_at_windowless_apphost_next_to_app()
    {
        var exe = CullLauncher.McpServerExe();
        Assert.EndsWith(Path.Combine("mcp", "Monocle.Mcp.exe"), exe);
    }

    [Fact]
    public void WriteMcpConfig_command_is_the_exe_with_no_dotnet_host()
    {
        var path = CullLauncher.WriteMcpConfig();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var monocle = doc.RootElement.GetProperty("mcpServers").GetProperty("monocle");
            var command = monocle.GetProperty("command").GetString();

            Assert.EndsWith("Monocle.Mcp.exe", command);
            Assert.DoesNotContain("dotnet", command!.ToLowerInvariant());
            // args must not smuggle a .dll back in via the dotnet muxer
            var args = monocle.GetProperty("args").EnumerateArray();
            foreach (var a in args)
                Assert.DoesNotContain(".dll", a.GetString()!);
        }
        finally { File.Delete(path); }
    }
}
