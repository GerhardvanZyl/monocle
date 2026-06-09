using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Monocle's locked-down photo-tools MCP server. Launched by the /cull job via .mcp.json so the
// cull can ONLY scan/score/rate photos and nothing else (#11). Logs go to stderr so stdout
// stays a clean JSON-RPC stream.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddSingleton<Monocle.Mcp.ShootState>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
