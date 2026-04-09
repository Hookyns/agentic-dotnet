using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

[McpServerToolType]
public sealed class NavigationTools(WorkspaceService workspace)
{
    // ── Projects ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_solution_projects")]
    [Description(
        "List all projects in the solution with their paths, assembly names, and output kind."
    )]
    public async Task<string> GetSolutionProjects(CancellationToken cancellationToken = default)
    {
        var solution = await workspace.GetSolutionAsync(cancellationToken);

        var projects = solution
            .Projects.Select(p => new
            {
                Name = p.Name,
                FilePath = p.FilePath,
                AssemblyName = p.AssemblyName,
                Language = p.Language,
                OutputKind = p.CompilationOptions?.OutputKind.ToString(),
            })
            .ToList();

        return JsonSerializer.Serialize(projects, JsonOptions.Options);
    }

    // ── Namespaces ────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_project_namespaces")]
    [Description("Get every namespace declared in a project, sorted alphabetically.")]
    public async Task<string> GetProjectNamespaces(
        [Description(
            "Project name as it appears in the solution (use get_solution_projects to find names)."
        )]
            string projectName,
        CancellationToken cancellationToken = default
    )
    {
        var project = await ResolveProjectAsync(projectName, cancellationToken);
        if (project is null)
            return NotFoundMessage("Project", projectName, "get_solution_projects");

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Could not compile project.";

        var namespaces = new SortedSet<string>();
        CollectNamespaces(compilation.GlobalNamespace, namespaces);
        return JsonSerializer.Serialize(namespaces, JsonOptions.Options);
    }

    // ── Types in namespace ────────────────────────────────────────────────

    [McpServerTool(Name = "get_types_in_namespace")]
    [Description(
        "List all types (classes, interfaces, enums, structs, delegates) in a specific namespace within a project."
    )]
    public async Task<string> GetTypesInNamespace(
        [Description("Project name.")] string projectName,
        [Description("Fully qualified namespace (e.g. 'MyApp.Services').")] string namespaceName,
        CancellationToken cancellationToken = default
    )
    {
        var project = await ResolveProjectAsync(projectName, cancellationToken);
        if (project is null)
            return NotFoundMessage("Project", projectName, "get_solution_projects");

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Could not compile project.";

        var ns = ResolveNamespace(compilation.GlobalNamespace, namespaceName);
        if (ns is null)
            return $"Namespace '{namespaceName}' not found in project '{projectName}'. Use get_project_namespaces to list available namespaces.";

        var types = ns.GetTypeMembers()
            .Select(t => new
            {
                Name = t.Name,
                FullName = t.ToDisplayString(),
                Kind = t.TypeKind.ToString(),
                Accessibility = t.DeclaredAccessibility.ToString(),
                IsAbstract = t.IsAbstract,
                IsSealed = t.IsSealed,
                IsStatic = t.IsStatic,
                IsGeneric = t.IsGenericType,
                TypeParameters = t.TypeParameters.Select(tp => tp.Name).ToArray(),
            })
            .OrderBy(t => t.Name)
            .ToList();

        if (types.Count == 0)
            return $"No types declared directly in namespace '{namespaceName}'.";

        return JsonSerializer.Serialize(types, JsonOptions.Options);
    }

    // ── Type info ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_type_info")]
    [Description(
        "Get detailed information about a type: base type, interfaces, constructors, methods, "
            + "properties, fields, and events. Use the fully qualified metadata name."
    )]
    public async Task<string> GetTypeInfo(
        [Description(
            "Fully qualified type name as it appears in metadata (e.g. 'MyApp.Services.UserService' or 'MyApp.IRepository`1')."
        )]
            string typeFullName,
        CancellationToken cancellationToken = default
    )
    {
        var solution = await workspace.GetSolutionAsync(cancellationToken);

        INamedTypeSymbol? type = null;
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            type = compilation?.GetTypeByMetadataName(typeFullName);
            if (type is not null)
                break;
        }

        if (type is null)
            return $"Type '{typeFullName}' not found in solution.";

        var info = new
        {
            Name = type.Name,
            FullName = type.ToDisplayString(),
            Kind = type.TypeKind.ToString(),
            Namespace = type.ContainingNamespace.IsGlobalNamespace
                ? null
                : type.ContainingNamespace.ToDisplayString(),
            Accessibility = type.DeclaredAccessibility.ToString(),
            IsAbstract = type.IsAbstract,
            IsSealed = type.IsSealed,
            IsStatic = type.IsStatic,
            IsGeneric = type.IsGenericType,
            TypeParameters = type
                .TypeParameters.Select(tp => new
                {
                    Name = tp.Name,
                    Constraints = tp.ConstraintTypes.Select(c => c.ToDisplayString()).ToArray(),
                })
                .ToArray(),
            BaseType = type.BaseType?.ToDisplayString(),
            Interfaces = type.Interfaces.Select(i => i.ToDisplayString()).ToArray(),
            Constructors = type
                .Constructors.Where(c => !c.IsImplicitlyDeclared)
                .OrderBy(c => c.Parameters.Length)
                .Select(FormatConstructor)
                .ToArray(),
            Methods = type.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary)
                .OrderBy(m => m.Name)
                .Select(FormatMethod)
                .ToArray(),
            Properties = type.GetMembers()
                .OfType<IPropertySymbol>()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    Name = p.Name,
                    Type = p.Type.ToDisplayString(),
                    Accessors = BuildAccessorString(p),
                    Accessibility = p.DeclaredAccessibility.ToString(),
                    IsStatic = p.IsStatic,
                    IsAbstract = p.IsAbstract,
                    IsVirtual = p.IsVirtual,
                    IsOverride = p.IsOverride,
                })
                .ToArray(),
            Fields = type.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => !f.IsImplicitlyDeclared)
                .OrderBy(f => f.Name)
                .Select(f => new
                {
                    Name = f.Name,
                    Type = f.Type.ToDisplayString(),
                    Accessibility = f.DeclaredAccessibility.ToString(),
                    IsStatic = f.IsStatic,
                    IsReadOnly = f.IsReadOnly,
                    IsConst = f.IsConst,
                    ConstantValue = f.IsConst ? f.ConstantValue?.ToString() : null,
                })
                .ToArray(),
            Events = type.GetMembers()
                .OfType<IEventSymbol>()
                .OrderBy(e => e.Name)
                .Select(e => new
                {
                    Name = e.Name,
                    Type = e.Type.ToDisplayString(),
                    Accessibility = e.DeclaredAccessibility.ToString(),
                    IsStatic = e.IsStatic,
                    IsAbstract = e.IsAbstract,
                    IsVirtual = e.IsVirtual,
                })
                .ToArray(),
        };

        return JsonSerializer.Serialize(info, JsonOptions.Options);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task<Project?> ResolveProjectAsync(string projectName, CancellationToken ct)
    {
        var solution = await workspace.GetSolutionAsync(ct);
        return solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static void CollectNamespaces(INamespaceSymbol ns, SortedSet<string> result)
    {
        if (!ns.IsGlobalNamespace)
            result.Add(ns.ToDisplayString());
        foreach (var child in ns.GetNamespaceMembers())
            CollectNamespaces(child, result);
    }

    private static INamespaceSymbol? ResolveNamespace(INamespaceSymbol root, string name)
    {
        var current = root;
        foreach (var part in name.Split('.'))
        {
            current = current
                .GetNamespaceMembers()
                .FirstOrDefault(n => string.Equals(n.Name, part, StringComparison.Ordinal));
            if (current is null)
                return null;
        }
        return current;
    }

    private static object FormatConstructor(IMethodSymbol ctor) =>
        new
        {
            Accessibility = ctor.DeclaredAccessibility.ToString(),
            Parameters = FormatParameters(ctor.Parameters),
        };

    private static object FormatMethod(IMethodSymbol m) =>
        new
        {
            Name = m.Name,
            ReturnType = m.ReturnType.ToDisplayString(),
            Parameters = FormatParameters(m.Parameters),
            TypeParameters = m.TypeParameters.Select(tp => tp.Name).ToArray(),
            Accessibility = m.DeclaredAccessibility.ToString(),
            IsStatic = m.IsStatic,
            IsAbstract = m.IsAbstract,
            IsVirtual = m.IsVirtual,
            IsOverride = m.IsOverride,
            IsAsync = m.IsAsync,
            IsExtensionMethod = m.IsExtensionMethod,
        };

    private static object[] FormatParameters(
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> ps
    ) =>
        ps.Select(p =>
                new
                {
                    Name = p.Name,
                    Type = p.Type.ToDisplayString(),
                    IsParams = p.IsParams,
                    IsOptional = p.IsOptional,
                    DefaultValue = p.HasExplicitDefaultValue
                        ? p.ExplicitDefaultValue?.ToString()
                        : null,
                } as object
            )
            .ToArray();

    private static string BuildAccessorString(IPropertySymbol p)
    {
        var parts = new List<string>();
        if (p.GetMethod is not null)
            parts.Add(
                p.GetMethod.DeclaredAccessibility == p.DeclaredAccessibility
                    ? "get"
                    : $"{p.GetMethod.DeclaredAccessibility.ToString().ToLower()} get"
            );
        if (p.SetMethod is not null)
            parts.Add(
                p.SetMethod.DeclaredAccessibility == p.DeclaredAccessibility
                    ? "set"
                    : $"{p.SetMethod.DeclaredAccessibility.ToString().ToLower()} set"
            );
        return string.Join("; ", parts) + ";";
    }

    private static string NotFoundMessage(string kind, string name, string discoverTool) =>
        $"{kind} '{name}' not found. Use {discoverTool} to list available {kind.ToLower()}s.";
}
