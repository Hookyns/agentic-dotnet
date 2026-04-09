using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;

// MSBuildLocator.RegisterDefaults() MUST be the first call that touches MSBuild/Roslyn types.
// Using directives above are compile-time only and do not trigger type loading at startup.
MSBuildLocator.RegisterDefaults();

if (args.Length == 0 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet agentic <path-to-solution.sln|.slnx>");
    return 1;
}

var solutionPath = Path.GetFullPath(args[0]);

var builder = Host.CreateApplicationBuilder(args);

// MCP communicates over stdio — suppress console logging to avoid corrupting the protocol.
// Workspace failure warnings are written directly to stderr inside WorkspaceService.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.None);

// WorkspaceService is registered via factory so the container owns lifetime (and calls Dispose).
builder.Services.AddSingleton<WorkspaceService>(_ => new WorkspaceService(solutionPath));

builder
    .Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DiagnosticTools>()
    .WithTools<CodeFixesTools>()
    .WithTools<NavigationTools>()
    .WithTools<SymbolTools>();

await builder.Build().RunAsync();
return 0;
