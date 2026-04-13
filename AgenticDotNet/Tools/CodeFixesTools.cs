using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AgenticDotNet.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using ModelContextProtocol.Server;

namespace AgenticDotNet.Tools;

[McpServerToolType]
internal sealed class CodeFixesTools(WorkspaceService workspace)
{
	// Reflection-based provider discovery — works for providers with parameterless ctors.
	// Providers requiring MEF-injected services will fail Activator.CreateInstance and are skipped.
	private static IReadOnlyList<CodeFixProvider>? _sProviders;

	[McpServerTool(Name = "get_code_fixes")]
	[Description(
		"List available automated code fixes for diagnostics in a file. "
			+ "Returns each diagnostic with the titles of its applicable fixes. "
			+ "Use apply_code_fix to apply a specific fix."
	)]
	public async Task<string> GetCodeFixes(
		[Description("Absolute path to the .cs file to inspect.")] string filePath,
		CancellationToken cancellationToken = default
	)
	{
		var solution = await workspace.GetSolutionAsync(cancellationToken);
		var document = FindDocumentInSolution(solution, filePath);
		if (document is null)
			return $"File not found in solution: {filePath}";

		var project = document.Project;
		var compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
			return "Could not compile project.";

		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var allDiagnostics = await GetFileDiagnosticsAsync(project, compilation, tree, cancellationToken);
		var diagnostics = allDiagnostics
			.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
			.ToImmutableArray();

		if (diagnostics.IsEmpty)
			return "No errors or warnings in file — no code fixes available.";

		var providers = GetCodeFixProviders();
		var results = new List<object>();

		foreach (var diag in diagnostics)
		{
			var fixTitles = new List<string>();
			var applicable = providers.Where(p => p.FixableDiagnosticIds.Contains(diag.Id));

			foreach (var provider in applicable)
			{
				var ctx = new CodeFixContext(
					document,
					diag,
					(action, _) => CollectTitles(action, fixTitles),
					cancellationToken
				);
				try
				{
					await provider.RegisterCodeFixesAsync(ctx);
				}
				catch
				{ /* provider may fail for unsupported scenarios */
				}
			}

			var span = diag.Location.GetLineSpan();
			results.Add(
				new
				{
					DiagnosticId = diag.Id,
					Severity = diag.Severity.ToString(),
					Message = diag.GetMessage(CultureInfo.InvariantCulture),
					Line = span.StartLinePosition.Line + 1,
					Column = span.StartLinePosition.Character + 1,
					AvailableFixes = fixTitles,
				}
			);
		}

		return JsonSerializer.Serialize(results, JsonOptions.Options);
	}

	[McpServerTool(Name = "apply_code_fix")]
	[Description(
		"Apply a specific code fix to a diagnostic in a file. "
			+ "Writes the changed files to disk and returns a summary. "
			+ "Use get_code_fixes first to discover the available fix titles."
	)]
	public async Task<string> ApplyCodeFix(
		[Description("Absolute path to the .cs file that contains the diagnostic.")] string filePath,
		[Description("Diagnostic ID to fix (e.g. 'CS0246').")] string diagnosticId,
		[Description("Exact title of the code fix to apply, as returned by get_code_fixes.")] string fixTitle,
		CancellationToken cancellationToken = default
	)
	{
		var solution = await workspace.GetSolutionAsync(cancellationToken);
		var document = FindDocumentInSolution(solution, filePath);
		if (document is null)
			return $"File not found in solution: {filePath}";

		var project = document.Project;
		var compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
			return "Could not compile project.";

		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var allDiagnostics = await GetFileDiagnosticsAsync(project, compilation, tree, cancellationToken);
		var diagnostic = allDiagnostics.FirstOrDefault(d => d.Id == diagnosticId);

		if (diagnostic is null)
			return $"Diagnostic '{diagnosticId}' not found in '{filePath}'.";

		var providers = GetCodeFixProviders().Where(p => p.FixableDiagnosticIds.Contains(diagnosticId));

		foreach (var provider in providers)
		{
			CodeAction? matched = null;

			var ctx = new CodeFixContext(
				document,
				diagnostic,
				(action, _) =>
				{
					if (matched is null)
						FindAction(action, fixTitle, ref matched);
				},
				cancellationToken
			);

			try
			{
				await provider.RegisterCodeFixesAsync(ctx);
			}
			catch
			{
				continue;
			}

			if (matched is null)
				continue;

			var operations = await matched.GetOperationsAsync(cancellationToken);
			var changedSolution = operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;

			if (changedSolution is null)
				continue;

			var changedFiles = await WriteChangedDocumentsAsync(solution, changedSolution, cancellationToken);

			return JsonSerializer.Serialize(
				new
				{
					Applied = fixTitle,
					DiagnosticId = diagnosticId,
					ChangedFiles = changedFiles,
				},
				JsonOptions.Options
			);
		}

		return $"No provider found for fix '{fixTitle}' on diagnostic '{diagnosticId}'.";
	}

	/// <summary>
	/// Recursively collects all leaf action titles (nested CodeActions are expanded).
	/// </summary>
	private static void CollectTitles(CodeAction action, List<string> titles)
	{
		var nested = action.NestedActions;
		if (nested.IsEmpty)
			titles.Add(action.Title);
		else
			foreach (var child in nested)
				CollectTitles(child, titles);
	}

	private static void FindAction(CodeAction action, string title, ref CodeAction? result)
	{
		if (result is not null)
			return;
		if (action.Title == title)
		{
			result = action;
			return;
		}
		foreach (var child in action.NestedActions)
			FindAction(child, title, ref result);
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
				var encoding = text.Encoding ?? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
				await File.WriteAllTextAsync(doc.FilePath, text.ToString(), encoding, ct);
				paths.Add(doc.FilePath);
			}
		}
		return [.. paths];
	}

	/// <summary>
	/// Returns all diagnostics for a specific syntax tree, including both compiler
	/// and analyzer diagnostics. Respects .editorconfig via project.AnalyzerOptions.
	/// </summary>
	private static async Task<IEnumerable<Diagnostic>> GetFileDiagnosticsAsync(
		Project project,
		Compilation compilation,
		SyntaxTree? tree,
		CancellationToken ct
	)
	{
		var compilerDiagnostics = compilation.GetDiagnostics(ct).Where(d => d.Location.SourceTree == tree);

		var analyzers = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language)).ToImmutableArray();

		if (analyzers.IsEmpty)
			return compilerDiagnostics;

		var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
		var analyzerDiagnostics = (await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct)).Where(d =>
			d.Location.SourceTree == tree
		);

		return compilerDiagnostics.Concat(analyzerDiagnostics);
	}

	private static IReadOnlyList<CodeFixProvider> GetCodeFixProviders()
	{
		if (_sProviders is not null)
			return _sProviders;

		var result = new List<CodeFixProvider>();
		var baseType = typeof(CodeFixProvider);

		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			var name = assembly.GetName().Name ?? string.Empty;
			if (!name.Contains("CodeAnalysis", StringComparison.OrdinalIgnoreCase))
				continue;

			Type[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch
			{
				continue;
			}

			foreach (var type in types)
			{
				if (type.IsAbstract || !baseType.IsAssignableFrom(type))
					continue;
				try
				{
					if (Activator.CreateInstance(type) is CodeFixProvider p)
						result.Add(p);
				}
				catch
				{ /* provider requires constructor args — skip */
				}
			}
		}

		_sProviders = result;
		return result;
	}

	private static Document? FindDocumentInSolution(Solution solution, string filePath) =>
		solution
			.Projects.SelectMany(p => p.Documents)
			.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
}
