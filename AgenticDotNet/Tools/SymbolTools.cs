using System.ComponentModel;
using System.Text.Json;
using AgenticDotNet.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;

namespace AgenticDotNet.Tools;

[McpServerToolType]
public sealed class SymbolTools(WorkspaceService workspace)
{
    [McpServerTool(Name = "find_symbol")]
    [Description(
        "Find symbols in the solution by name. "
            + "Searches classes, interfaces, enums, structs, delegates, methods, properties, fields. "
            + "The search is case-insensitive and matches partial names. Returns up to 100 results."
    )]
    public async Task<string> FindSymbol(
        [Description("Symbol name to search for (partial match, case-insensitive).")] string name,
        [Description(
            "Optional kind filter: 'class', 'interface', 'enum', 'struct', 'delegate', 'method', 'property', 'field', 'event'. Omit for all kinds."
        )]
            string? kind = null,
        CancellationToken cancellationToken = default
    )
    {
        var solution = await workspace.GetSolutionAsync(cancellationToken);

        var filter = kind?.ToLowerInvariant() switch
        {
            "class" or "interface" or "enum" or "struct" or "delegate" => SymbolFilter.Type,
            "method" or "property" or "field" or "event" => SymbolFilter.Member,
            _ => SymbolFilter.TypeAndMember,
        };

        // FindDeclarationsAsync scopes to a single project; aggregate across all projects.
        var symbolBag = new List<ISymbol>();
        foreach (var project in solution.Projects)
        {
            var found = await SymbolFinder.FindDeclarationsAsync(
                project,
                name,
                ignoreCase: true,
                filter,
                cancellationToken
            );
            symbolBag.AddRange(found);
        }
        var symbols = symbolBag.DistinctBy(s => s.ToDisplayString());

        var results = symbols
            .Where(s => kind is null || MatchesKind(s, kind))
            .Take(100)
            .Select(s =>
            {
                var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
                var span = loc?.GetLineSpan();
                return (object)
                    new
                    {
                        Name = s.Name,
                        FullName = s.ToDisplayString(),
                        Kind = SymbolKindLabel(s),
                        FilePath = span?.Path,
                        Line = span.HasValue ? span.Value.StartLinePosition.Line + 1 : (int?)null,
                        Column = span.HasValue
                            ? span.Value.StartLinePosition.Character + 1
                            : (int?)null,
                        ContainingType = s.ContainingType?.ToDisplayString(),
                        Namespace = s.ContainingNamespace?.IsGlobalNamespace == false
                            ? s.ContainingNamespace.ToDisplayString()
                            : null,
                    };
            })
            .ToList();

        if (results.Count == 0)
            return kind is not null
                ? $"No {kind} symbols matching '{name}' found."
                : $"No symbols matching '{name}' found.";

        return JsonSerializer.Serialize(results, JsonOptions.Options);
    }

    [McpServerTool(Name = "rename_symbol")]
    [Description(
        "Rename a symbol (class, method, property, variable, etc.) across the entire solution. "
            + "Changes are written to disk. Returns the list of modified files."
    )]
    public async Task<string> RenameSymbol(
        [Description("Absolute path to the .cs file where the symbol is declared or referenced.")]
            string filePath,
        [Description("1-based line number of the symbol.")] int line,
        [Description("1-based column number of the symbol.")] int column,
        [Description("New name to give the symbol.")] string newName,
        CancellationToken cancellationToken = default
    )
    {
        var solution = await workspace.GetSolutionAsync(cancellationToken);

        var document = FindDocument(solution, filePath);
        if (document is null)
            return $"File not found in solution: {filePath}";

        var symbol = await FindSymbolAtAsync(document, line, column, cancellationToken);
        if (symbol is null)
            return $"No symbol found at line {line}, column {column} in '{filePath}'.";

        var oldName = symbol.Name;
        if (oldName == newName)
            return $"Symbol is already named '{newName}'.";

        var options = new SymbolRenameOptions(
            RenameOverloads: false,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: true
        );

        Solution renamedSolution;
        try
        {
            renamedSolution = await Renamer.RenameSymbolAsync(
                solution,
                symbol,
                options,
                newName,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            return $"Rename failed: {ex.Message}";
        }

        var changedFiles = await WriteChangedDocumentsAsync(
            solution,
            renamedSolution,
            cancellationToken
        );

        return JsonSerializer.Serialize(
            new
            {
                OldName = oldName,
                NewName = newName,
                ChangedFiles = changedFiles,
            },
            JsonOptions.Options
        );
    }

    [McpServerTool(Name = "find_implementations")]
    [Description(
        "Find all types that implement an interface or inherit from a base class. "
            + "For interfaces: finds implementing classes. "
            + "For classes: finds all derived (sub)classes transitively."
    )]
    public async Task<string> FindImplementations(
        [Description(
            "Fully qualified name of the interface or base class (e.g. 'MyApp.IRepository' or 'MyApp.BaseService')."
        )]
            string typeFullName,
        CancellationToken cancellationToken = default
    )
    {
        var solution = await workspace.GetSolutionAsync(cancellationToken);

        var type = await FindTypeAsync(solution, typeFullName, cancellationToken);
        if (type is null)
            return $"Type '{typeFullName}' not found in solution.";

        var all = new Dictionary<string, object>();

        if (type.TypeKind == TypeKind.Interface)
        {
            var impls = await SymbolFinder.FindImplementationsAsync(
                type,
                solution,
                cancellationToken: cancellationToken
            );
            foreach (var impl in impls)
                AddResult(all, impl);
        }

        // Always look for derived classes too (covers abstract base + interface impls via inheritance)
        var derived = await SymbolFinder.FindDerivedClassesAsync(
            type,
            solution,
            transitive: true,
            cancellationToken: cancellationToken
        );
        foreach (var d in derived)
            AddResult(all, d);

        if (all.Count == 0)
            return $"No implementations or derived types found for '{typeFullName}'.";

        return JsonSerializer.Serialize(all.Values.ToList(), JsonOptions.Options);
    }

    [McpServerTool(Name = "find_overrides")]
    [Description(
        "Find all overrides of a virtual or abstract method in derived types across the solution."
    )]
    public async Task<string> FindOverrides(
        [Description(
            "Fully qualified name of the type that declares the method (e.g. 'MyApp.BaseService')."
        )]
            string typeFullName,
        [Description("Name of the method to find overrides for.")] string methodName,
        CancellationToken cancellationToken = default
    )
    {
        var solution = await workspace.GetSolutionAsync(cancellationToken);

        var type = await FindTypeAsync(solution, typeFullName, cancellationToken);
        if (type is null)
            return $"Type '{typeFullName}' not found in solution.";

        var method = type.GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary);

        if (method is null)
            return $"Method '{methodName}' not found in '{typeFullName}'.";

        if (!method.IsVirtual && !method.IsAbstract && !method.IsOverride)
            return $"Method '{typeFullName}.{methodName}' is not virtual, abstract, or an override — no overrides can exist.";

        var overrides = await SymbolFinder.FindOverridesAsync(
            method,
            solution,
            cancellationToken: cancellationToken
        );

        var results = overrides
            .OfType<IMethodSymbol>()
            .Select(o =>
            {
                var loc = o.Locations.FirstOrDefault(l => l.IsInSource);
                var span = loc?.GetLineSpan();
                return (object)
                    new
                    {
                        ContainingType = o.ContainingType.ToDisplayString(),
                        Signature = o.ToDisplayString(),
                        FilePath = span?.Path,
                        Line = span.HasValue ? span.Value.StartLinePosition.Line + 1 : (int?)null,
                        IsAbstract = o.IsAbstract,
                        IsSealed = o.IsSealed,
                    };
            })
            .ToList();

        if (results.Count == 0)
            return $"No overrides found for '{typeFullName}.{methodName}'.";

        return JsonSerializer.Serialize(results, JsonOptions.Options);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static async Task<ISymbol?> FindSymbolAtAsync(
        Document document,
        int line,
        int column,
        CancellationToken ct
    )
    {
        var text = await document.GetTextAsync(ct);
        if (line < 1 || line > text.Lines.Count)
            return null;

        var lineStart = text.Lines[line - 1].Start;
        var position = lineStart + Math.Max(0, column - 1);

        var semanticModel = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (semanticModel is null || root is null)
            return null;

        var token = root.FindToken(position);
        if (token.RawKind == 0)
            return null;

        var node = token.Parent;
        if (node is null)
            return null;

        return semanticModel.GetSymbolInfo(node, ct).Symbol
            ?? semanticModel.GetDeclaredSymbol(node, ct);
    }

    private static async Task<INamedTypeSymbol?> FindTypeAsync(
        Solution solution,
        string typeFullName,
        CancellationToken ct
    )
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            var symbol = compilation?.GetTypeByMetadataName(typeFullName);
            if (symbol is not null)
                return symbol;
        }
        return null;
    }

    private static async Task<string[]> WriteChangedDocumentsAsync(
        Solution original,
        Solution changed,
        CancellationToken ct
    )
    {
        var paths = new List<string>();
        foreach (var projectChanges in changed.GetChanges(original).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                var doc = changed.GetDocument(docId);
                if (doc?.FilePath is null)
                    continue;
                var text = await doc.GetTextAsync(ct);
                await File.WriteAllTextAsync(doc.FilePath, text.ToString(), ct);
                paths.Add(doc.FilePath);
            }
        }
        return [.. paths];
    }

    private static Document? FindDocument(Solution solution, string filePath) =>
        solution
            .Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d =>
                string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            );

    private static bool MatchesKind(ISymbol symbol, string kind) =>
        kind.ToLowerInvariant() switch
        {
            "class" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class },
            "interface" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface },
            "enum" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum },
            "struct" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Struct },
            "delegate" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate },
            "method" => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary },
            "property" => symbol is IPropertySymbol,
            "field" => symbol is IFieldSymbol,
            "event" => symbol is IEventSymbol,
            _ => true,
        };

    private static string SymbolKindLabel(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol t => t.TypeKind.ToString(),
            IMethodSymbol => "Method",
            IPropertySymbol => "Property",
            IFieldSymbol => "Field",
            IEventSymbol => "Event",
            INamespaceSymbol => "Namespace",
            _ => symbol.Kind.ToString(),
        };

    private static void AddResult(Dictionary<string, object> map, ISymbol symbol)
    {
        var key = symbol.ToDisplayString();
        if (map.ContainsKey(key))
            return;

        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var span = loc?.GetLineSpan();

        map[key] = new
        {
            Name = symbol.Name,
            FullName = key,
            Kind = SymbolKindLabel(symbol),
            FilePath = span?.Path,
            Line = span.HasValue ? span.Value.StartLinePosition.Line + 1 : (int?)null,
        };
    }
}
