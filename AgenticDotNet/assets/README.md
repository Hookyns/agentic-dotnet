# # Agentic .NET MCP (AgenticDotNet)

Roslyn-powered [MCP](https://modelcontextprotocol.io) server for .NET solutions.
Gives AI agents compiler-grade understanding of a C# codebase: diagnostics, type navigation, symbol search, and automated code fixes — all backed by the same incremental Roslyn pipeline used by Visual Studio.

## Setup

**Step 1 — Install the tool**

Global install (available to all projects on the machine):
```bash
dotnet tool install -g AgenticDotNet
```

Per-project install (version pinned in source control, recommended for teams):
```bash
# Create the manifest if one doesn't exist yet
dotnet new tool-manifest

# Install into .config/dotnet-tools.json
dotnet tool install AgenticDotNet
```

Commit `.config/dotnet-tools.json`. Teammates and CI can then restore with:
```bash
dotnet tool restore
```

**Step 2 — Register the server in your MCP client config**

The server communicates over stdio and takes the solution path as its only argument.

Global install:
```json
{
  "mcpServers": {
    "dotnet": {
      "command": "agentic",
      "args": ["/absolute/path/to/MySolution.sln"]
    }
  }
}
```

Per-project install (prefix with `dotnet`):
```json
{
  "mcpServers": {
    "dotnet": {
      "command": "dotnet",
      "args": ["agentic", "/absolute/path/to/MySolution.sln"]
    }
  }
}
```

Accepted solution formats: `.sln`, `.slnx`. The path may be absolute or relative to the working directory.

**Requirements:** .NET SDK must be installed. The solution must be restorable (`dotnet restore`).

---

## Available tools

### Diagnostics

**`get_diagnostics`**
Returns compiler errors/warnings (CS\*) and analyzer diagnostics (CA\*, IDE\*, and any custom analyzers) for the whole solution or a single file.
Respects the project's `AnalysisMode`, `AnalysisLevel`, and `.editorconfig` severity overrides exactly as `dotnet build` would.
- No arguments: errors and warnings across the whole solution.
- `filePath` (optional): all diagnostic severities for that specific `.cs` file.

**`get_code_fixes`**
Lists available automated code fix titles for every diagnostic in a file.
- `filePath`: absolute path to the `.cs` file to inspect.

**`apply_code_fix`**
Applies a specific code fix and writes the changed files to disk.
- `filePath`: file containing the diagnostic.
- `diagnosticId`: e.g. `CA1515`.
- `fixTitle`: exact title as returned by `get_code_fixes`.

### Navigation

**`get_solution_projects`**
Lists all projects in the solution with their file paths, assembly names, language, and output kind.

**`get_project_namespaces`**
All namespaces declared in a project, sorted alphabetically.
- `projectName`: as returned by `get_solution_projects`.

**`get_types_in_namespace`**
All types (classes, interfaces, enums, structs, delegates) directly in a namespace.
- `projectName`, `namespaceName`: fully qualified namespace (e.g. `MyApp.Services`).

**`get_type_info`**
Full metadata for a type: base type, interfaces, constructors, methods, properties, fields, events.
- `typeFullName`: fully qualified metadata name (e.g. `MyApp.Services.UserService`).

### Symbols

**`find_symbol`**
Case-insensitive partial-name search across the whole solution. Returns up to 100 results with file paths and line numbers.
- `name`: partial or full symbol name.
- `kind` (optional): `class`, `interface`, `enum`, `struct`, `delegate`, `method`, `property`, `field`, `event`.

**`find_implementations`**
All types that implement an interface or inherit from a base class (transitively).
- `typeFullName`: fully qualified name of the interface or base class.

**`find_overrides`**
All overrides of a virtual or abstract method across the solution.
- `typeFullName`: declaring type. `methodName`: method to look up.

**`rename_symbol`**
Renames a symbol across the entire solution and writes all affected files to disk.
- `filePath`: file where the symbol appears. `line`, `column`: 1-based position. `newName`: replacement name.

---

## Recommended agent workflow

```
get_diagnostics                        → identify errors and warnings
get_code_fixes filePath=...            → see what Roslyn can fix automatically
apply_code_fix filePath=... id= fix=   → apply the chosen fix
get_diagnostics                        → confirm the fix resolved the issue

find_symbol name=...                   → locate a type or member
get_type_info typeFullName=...         → inspect its full API surface
find_implementations typeFullName=...  → discover concrete classes
rename_symbol filePath=... line= col=  → safe cross-solution rename
```