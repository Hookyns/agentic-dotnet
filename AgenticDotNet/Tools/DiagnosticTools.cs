using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AgenticDotNet.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using ModelContextProtocol.Server;

namespace AgenticDotNet.Tools;

[McpServerToolType]
public sealed class DiagnosticTools(WorkspaceService workspace)
{
	[McpServerTool(Name = "get_diagnostics")]
	[Description(
		"Get compilation diagnostics from the solution, including both compiler errors/warnings "
			+ "and analyzer diagnostics (CA*, IDE*, etc.) as configured in the project. "
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

			// Load analyzers declared in the project (respects AnalysisMode/AnalysisLevel from .csproj).
			var analyzers = project
				.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language))
				.ToImmutableArray();

			if (filePath is not null)
			{
				var doc = FindDocument(project, filePath);
				if (doc is null)
					continue;

				var tree = await doc.GetSyntaxTreeAsync(cancellationToken);

				diagnostics = await GetAllDiagnosticsAsync(compilation, analyzers, cancellationToken);
				diagnostics = diagnostics.Where(d => d.Location.SourceTree == tree);
			}
			else
			{
				diagnostics = await GetAllDiagnosticsAsync(compilation, analyzers, cancellationToken);
				diagnostics = diagnostics.Where(d =>
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
						Message = d.GetMessage(CultureInfo.InvariantCulture),
						FilePath = span.Path,
						Line = span.StartLinePosition.Line + 1,
						Column = span.StartLinePosition.Character + 1,
						Project = project.Name,
					}
				);
			}
		}

		if (results.Count == 0)
		{
			var projectCount = solution.Projects.Count();
			var failures = workspace.WorkspaceFailures;

			if (projectCount == 0 || failures.Count > 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.AppendLine("No diagnostics found, but the workspace may not have loaded correctly.");
				sb.AppendLine(CultureInfo.InvariantCulture, $"Projects loaded: {projectCount}");
				if (failures.Count > 0)
				{
					sb.AppendLine("Workspace failures:");
					foreach (var f in failures)
						sb.AppendLine(CultureInfo.InvariantCulture, $"  {f}");
				}
				return sb.ToString().TrimEnd();
			}

			return filePath is not null
				? $"No diagnostics found in '{filePath}'."
				: $"No errors or warnings found in the solution ({projectCount} project(s) checked).";
		}

		return JsonSerializer.Serialize(results, JsonOptions.Options);
	}

	// ── Helpers ────────────────────────────────────────────────────────────

	private static async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(
		Compilation compilation,
		ImmutableArray<DiagnosticAnalyzer> analyzers,
		CancellationToken ct
	)
	{
		// Compiler diagnostics are always included.
		var compilerDiagnostics = compilation.GetDiagnostics(ct);

		if (analyzers.IsEmpty)
			return compilerDiagnostics;

		// Run project's own analyzers (CA*, IDE*, custom) on top of compilation.
		var withAnalyzers = compilation.WithAnalyzers(analyzers, options: null);
		var analyzerDiagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);

		return compilerDiagnostics.Concat(analyzerDiagnostics);
	}

	private static Document? FindDocument(Project project, string filePath) =>
		project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
}
