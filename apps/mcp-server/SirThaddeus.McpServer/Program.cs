using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SirThaddeus.McpServer.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(MetaTools).Assembly);

#if WINDOWS || NET8_0_WINDOWS10_0_19041_0
mcpBuilder.WithToolsFromAssembly(typeof(ScreenTools).Assembly);
#endif

await builder.Build().RunAsync();
