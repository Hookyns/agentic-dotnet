using System.Reflection;
using AgenticDotNet.Services;
using AgenticDotNet.Tools;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MSBuildLocator.RegisterDefaults() MUST be the first call that touches MSBuild/Roslyn types.
// Using directives above are compile-time only and do not trigger type loading at startup.
MSBuildLocator.RegisterDefaults();

if (args.Length == 0)
{
	await Console.Error.WriteLineAsync("Usage: agentic <path-to-solution.sln|.slnx>");
	return 1;
}

var solutionPath = Path.GetFullPath(args[0]);

if (!File.Exists(solutionPath))
{
	await Console.Error.WriteLineAsync($"Solution file not found: {solutionPath}");
	return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// MCP communicates over stdio — suppress console logging to avoid corrupting the protocol.
// Workspace failure warnings are written directly to stderr inside WorkspaceService.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.None);

// WorkspaceService is registered via factory so the container owns lifetime (and calls Dispose).
builder.Services.AddSingleton<WorkspaceService>(_ => new WorkspaceService(solutionPath));

var assembly = Assembly.GetExecutingAssembly();
var version =
	assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
	?? assembly.GetName().Version?.ToString()
	?? "1.0.0";

builder
	.Services.AddMcpServer(options =>
	{
		options.ServerInfo = new() { Name = "Agentic .NET", Version = version };
		options.ServerInstructions = """
		Agentic .NET is a Roslyn-powered MCP server that gives you live access to a
		loaded .NET solution — diagnostics, type navigation, symbol search, and automated
		code fixes — all sourced from the actual compilation.

		═══════════════════════════════════════════════════════════════════════════════
		🚨 USE THIS TOOL FIRST — BEFORE GUESSING OR EDITING BLINDLY
		═══════════════════════════════════════════════════════════════════════════════

		When working on a .NET solution, you MUST use this tool BEFORE:
		❌ Guessing type or member names from memory
		❌ Running `dotnet build` to check for errors — use get_diagnostics instead
		❌ Performing a rename or refactor without knowing all call sites
		❌ Applying a manual fix for a diagnostic without checking available code fixes

		WHY? The server reflects the REAL compilation state:
		✅ Exact error and warning list (compiler + analyzers, respects .editorconfig)
		✅ Faster than a full `dotnet build` — no subprocess, no output binary written
		✅ Exact type/member names, signatures, and locations
		✅ All implementations, derived types, and overrides across the solution
		✅ Automated code fixes registered by Roslyn providers

		═══════════════════════════════════════════════════════════════════════════════
		🎯 WHEN TO USE
		═══════════════════════════════════════════════════════════════════════════════

		USE PROACTIVELY — before writing or editing code:
		- Need to know what types exist in a namespace or project
		- About to reference a type or member whose exact name is uncertain
		- Planning a rename, move, or refactor

		AFTER every implementation or .cs file change — instead of `dotnet build`:
		- Call get_diagnostics to verify correctness before proceeding
		- Call get_code_fixes if any errors or warnings appear
		- Fix all errors before moving on to the next change

		USE REACTIVELY — when you encounter:
		- Build errors or warnings you need to understand or fix
		- Need to find all implementations of an interface
		- Need to find all overrides of a virtual method
		- Need to locate where a symbol is declared

		═══════════════════════════════════════════════════════════════════════════════
		⚡ QUICK WORKFLOWS
		═══════════════════════════════════════════════════════════════════════════════

		After any .cs file change (replaces `dotnet build`):
		   1. get_diagnostics                        → verify — no errors before moving on
		   2. get_code_fixes(filePath)               → see available automated fixes
		   3. apply_code_fix(filePath, id, title)    → apply the fix

		Explore the solution:
		   1. get_solution_projects                  → list projects
		   2. get_project_namespaces(project)        → list namespaces
		   3. get_types_in_namespace(project, ns)    → list types
		   4. get_type_info(fullName)                → members & signatures

		Find and navigate symbols:
		   1. find_symbol(name, kind?)               → locate by name (partial match)
		   2. find_implementations(typeFullName)     → all implementors / subclasses
		   3. find_overrides(typeFullName, method)   → all overrides of a method

		Safe rename:
		   1. find_symbol(name)                      → confirm declaration location
		   2. rename_symbol(filePath, line, col, newName) → renames across solution

		═══════════════════════════════════════════════════════════════════════════════
		📋 TOOL REFERENCE
		═══════════════════════════════════════════════════════════════════════════════

		Diagnostics & Fixes:
		• get_diagnostics([filePath])      — errors/warnings for whole solution or one file
		• get_code_fixes(filePath)         — automated fixes available for a file's diagnostics
		• apply_code_fix(...)              — apply a specific fix by diagnostic ID + title

		Navigation:
		• get_solution_projects            — all projects with paths and output kinds
		• get_project_namespaces(project)  — sorted namespace list for a project
		• get_types_in_namespace(...)      — types declared in a namespace
		• get_type_info(fullName)          — full member listing for a type

		Symbol Search & Refactoring:
		• find_symbol(name, [kind])        — find by name (partial, case-insensitive)
		• find_implementations(type)       — implementors of an interface or subclasses
		• find_overrides(type, method)     — all overrides of a virtual/abstract method
		• rename_symbol(file, line, col, newName) — safe solution-wide rename

		═══════════════════════════════════════════════════════════════════════════════
		💡 REMEMBER
		═══════════════════════════════════════════════════════════════════════════════

		• NEVER run `dotnet build` to check correctness — use get_diagnostics instead;
		  it is faster (no subprocess, no binary output) and has richer structured output
		• Diagnostics include both compiler (CS*) and analyzer (CA*, IDE*) results
		• get_diagnostics without a file path returns only errors and warnings;
		  with a file path it returns all severities including Info and Hidden
		• rename_symbol writes changes to disk — use find_symbol first to confirm the target
		• Type names passed to get_type_info and find_implementations must be
		  fully-qualified metadata names (e.g. "MyApp.Services.UserService")
		""";
	})
	.WithStdioServerTransport()
	.WithTools<DiagnosticTools>()
	.WithTools<CodeFixesTools>()
	.WithTools<NavigationTools>()
	.WithTools<SymbolTools>();

await builder.Build().RunAsync();
return 0;
