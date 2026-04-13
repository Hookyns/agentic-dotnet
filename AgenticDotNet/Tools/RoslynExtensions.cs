using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AgenticDotNet.Tools;

internal static class RoslynExtensions
{
	/// <summary>
	/// Finds a document in the solution by file path (case-insensitive).
	/// </summary>
	internal static Document? FindDocument(this Solution solution, string filePath) =>
		solution
			.Projects.SelectMany(p => p.Documents)
			.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Finds a named type symbol across all projects in the solution by metadata name.
	/// </summary>
	internal static async Task<INamedTypeSymbol?> FindTypeAsync(
		this Solution solution,
		string typeFullName,
		CancellationToken ct
	)
	{
		foreach (var project in solution.Projects)
		{
			var compilation = await project.GetCompilationAsync(ct);
			var symbol = compilation?.GetTypeByMetadataName(typeFullName);
			if (symbol is not null)
			{
				return symbol;
			}
		}

		return null;
	}

	/// <summary>
	/// Returns all diagnostics for a project (compiler + analyzer), respecting
	/// .editorconfig severity overrides via <see cref="Project.AnalyzerOptions"/>.
	/// </summary>
	internal static async Task<IEnumerable<Diagnostic>> GetDiagnosticsAsync(
		this Project project,
		Compilation compilation,
		CancellationToken ct
	)
	{
		var compilerDiagnostics = compilation.GetDiagnostics(ct);

		var analyzers = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language)).ToImmutableArray();

		if (analyzers.IsEmpty)
		{
			return compilerDiagnostics;
		}

		var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
		var analyzerDiagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);

		return compilerDiagnostics.Concat(analyzerDiagnostics);
	}

	/// <summary>
	/// Writes all documents that changed between <paramref name="original"/> and
	/// <paramref name="changed"/> to disk, preserving each file's original encoding
	/// (including UTF-8 BOM and code-page encodings such as CP1250).
	/// </summary>
	internal static async Task<string[]> WriteChangedDocumentsAsync(
		this Solution original,
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
				{
					continue;
				}

				var text = await doc.GetTextAsync(ct);
				var encoding = text.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
				await File.WriteAllTextAsync(doc.FilePath, text.ToString(), encoding, ct);
				paths.Add(doc.FilePath);
			}
		}

		return [.. paths];
	}
}
