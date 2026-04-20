using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using SirThaddeus.Logging;
using SirThaddeus.McpServer.Tools;

var builder = Host.CreateApplicationBuilder(args);

// stdio is the MCP transport here, so every log line must go to stderr.
builder.UseSirThaddeusLogging(new LoggingOptions
{
    ComponentName = "mcp-server",
    ConsoleStandardErrorOnly = true,
});

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(MetaTools).Assembly);

#if WINDOWS || NET8_0_WINDOWS10_0_19041_0
mcpBuilder.WithToolsFromAssembly(typeof(ScreenTools).Assembly);
#endif

await builder.Build().RunAsync();
