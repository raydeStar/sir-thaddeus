using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ─────────────────────────────────────────────────────────────────────
// MCP Server Entry Point
//
// Launched as a child process by the desktop runtime (or any MCP client).
// Communicates via stdin/stdout using JSON-RPC per the MCP specification.
// stderr is used for logging so it doesn't pollute the protocol stream.
// ─────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(consoleLogOptions =>
{
    // Route ALL logs to stderr so stdout stays clean for JSON-RPC.
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
