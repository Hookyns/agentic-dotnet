# Agentic .NET MCP (AgenticDotNet)

A Roslyn-powered [MCP](https://modelcontextprotocol.io) server that gives AI agents semantic understanding of a .NET solution — diagnostics, navigation, symbol search, and automated code fixes — backed by the same compiler pipeline used by Visual Studio and Rider.

## Installation

### Global

```bash
dotnet tool install -g AgenticDotNet
```

### Per-project (tool manifest)

This is the recommended approach for team projects — the tool version is pinned in source control and anyone can restore it with a single command.

```bash
# Create the manifest if one doesn't exist yet
dotnet new tool-manifest

# Install into the manifest (.config/dotnet-tools.json)
dotnet tool install AgenticDotNet
```

Commit `.config/dotnet-tools.json`. Anyone who clones the repo can then run:

```bash
dotnet tool restore
```

When using a local install, invoke the tool with `dotnet` prefix:

```bash
dotnet agentic <path-to-solution.sln|.slnx>
```

And use `dotnet agentic` as the command in your MCP config (see below).

## Usage

```bash
agentic <path-to-solution.sln|.slnx>
```

The path can be absolute or relative to the current working directory. The server communicates over **stdio** (MCP standard transport), so it is meant to be launched by an MCP client, not used interactively.

### Example MCP client config (Claude Desktop / Claude Code)

Global install:
```json
{
  "mcpServers": {
    "agentic-dotnet": {
      "command": "agentic",
      "args": ["C:/Work/MyProject/MyProject.sln"]
    }
  }
}
```

Local (per-project) install — run `dotnet tool restore` first:
```json
{
  "mcpServers": {
    "agentic-dotnet": {
      "command": "dotnet",
      "args": ["agentic", "C:/Work/MyProject/MyProject.sln"]
    }
  }
}
```

## Tools

### Diagnostics

| Tool              | Description                                                                                                                                                                                  |
|-------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `get_diagnostics` | Compiler errors/warnings and analyzer diagnostics (CA\*, IDE\*) for the whole solution or a specific file. Respects `AnalysisMode`, `AnalysisLevel`, and `.editorconfig` severity overrides. |
| `get_code_fixes`  | Lists available automated code fixes for diagnostics in a file.                                                                                                                              |
| `apply_code_fix`  | Applies a specific code fix and writes changes to disk.                                                                                                                                      |

### Navigation

| Tool                     | Description                                                                                 |
|--------------------------|---------------------------------------------------------------------------------------------|
| `get_solution_projects`  | Lists all projects with paths, assembly names, and output kind.                             |
| `get_project_namespaces` | All namespaces declared in a project, sorted alphabetically.                                |
| `get_types_in_namespace` | All types (classes, interfaces, enums, structs, delegates) in a namespace.                  |
| `get_type_info`          | Full type detail: base type, interfaces, constructors, methods, properties, fields, events. |

### Symbols

| Tool                   | Description                                                                                                         |
|------------------------|---------------------------------------------------------------------------------------------------------------------|
| `find_symbol`          | Case-insensitive partial-name search across the solution. Supports kind filters (`class`, `method`, `property`, …). |
| `find_implementations` | All types that implement an interface or derive from a class (transitive).                                          |
| `find_overrides`       | All overrides of a virtual or abstract method.                                                                      |
| `rename_symbol`        | Renames a symbol across the entire solution and writes all changed files to disk.                                   |

## How it works

On the first tool call the server opens the solution via `MSBuildWorkspace`, which uses the installed .NET SDK to evaluate all project files. Subsequent calls do an **incremental refresh**: only files whose `LastWriteTimeUtc` changed since the last call are re-parsed. Roslyn reuses all unchanged compilation state, so repeated calls are fast — the same incremental pipeline that powers IDE analyzers.

Analyzer diagnostics are produced by running `project.AnalyzerReferences` through `CompilationWithAnalyzers` with `project.AnalyzerOptions`, which includes the `.editorconfig` provider. This means severity overrides, code-style rules, and custom analyzers all behave exactly as they do during `dotnet build`.

Source-generated files are included: `get_diagnostics` covers generated syntax trees, and file-specific queries fall back through `GetSourceGeneratedDocumentsAsync` and the raw compilation tree list.

## Development

### Build

```bash
dotnet build
```

### Debug with MCP Inspector

[MCP Inspector](https://github.com/modelcontextprotocol/inspector) lets you call tools interactively against a running server:

```bash
npx @modelcontextprotocol/inspector .\AgenticDotNet\bin\Debug\net10.0\agentic.exe .\AgenticDotNet.slnx
```

Or after `dotnet pack` + local tool install:

```bash
dotnet tool install -g AgenticDotNet --add-source ./AgenticDotNet/nupkg
npx @modelcontextprotocol/inspector agentic .\AgenticDotNet.slnx
```

The Inspector opens a browser UI where you can invoke each tool and inspect the JSON request/response.