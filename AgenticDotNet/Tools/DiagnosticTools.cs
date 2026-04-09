using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

[McpServerToolType]
public sealed class DiagnosticTools(WorkspaceService workspace)
{
    [McpServerTool(Name = "get_diagnostics")]
    [Description(
        "Get compilation diagnostics from the solution. "
            + "Without a file path: returns errors and warnings for the whole solution. "
            + "With a file path: returns all severities (including Info and Hidden) for that file only."
    )]
    public async Task<string> GetDiagnostics(
        [Description(
            "Optional: absolute path to a .cs file. When supplied, all diagnostic severities are returned for that file."
        )]
            string? filePath = null,
        CancellationToken cancellationToken = default
    )
    {
        var solution = await workspace.GetSolutionAsync(cancellationToken);
        var results = new List<object>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;

            IEnumerable<Diagnostic> diagnostics;

            if (filePath is not null)
            {
                var doc = FindDocument(project, filePath);
                if (doc is null)
                    continue;

                var tree = await doc.GetSyntaxTreeAsync(cancellationToken);
                diagnostics = compilation
                    .GetDiagnostics(cancellationToken)
                    .Where(d => d.Location.SourceTree == tree);
            }
            else
            {
                // Whole-solution scan: only surfaced severities
                diagnostics = compilation
                    .GetDiagnostics(cancellationToken)
                    .Where(d =>
                        d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning
                    );
            }

            foreach (var d in diagnostics)
            {
                var span = d.Location.GetLineSpan();
                results.Add(
                    new
                    {
                        Id = d.Id,
                        Severity = d.Severity.ToString(),
                        Message = d.GetMessage(),
                        FilePath = span.Path,
                        Line = span.StartLinePosition.Line + 1,
                        Column = span.StartLinePosition.Character + 1,
                        Project = project.Name,
                    }
                );
            }
        }

        if (results.Count == 0)
            return filePath is not null
                ? $"No diagnostics found in '{filePath}'."
                : "No errors or warnings found in the solution.";

        return JsonSerializer.Serialize(results, JsonOptions.Options);
    }

    private static Document? FindDocument(Project project, string filePath) =>
        project.Documents.FirstOrDefault(d =>
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
        );
}
