using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace AgenticDotNet.Services;

/// <summary>
/// Manages a single MSBuildWorkspace for the solution path supplied at startup.
/// Every call to GetSolutionAsync refreshes any files that changed on disk since the
/// last call, so Roslyn's incremental compilation only re-parses what is actually new.
/// </summary>
internal sealed class WorkspaceService : IDisposable
{
	private static readonly Lazy<ImmutableArray<Assembly>> SMefAssemblies = new(
		BuildMefAssemblies,
		LazyThreadSafetyMode.ExecutionAndPublication
	);

	private readonly string _solutionPath;
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly Dictionary<string, DateTime> _fileTimestamps = new(StringComparer.OrdinalIgnoreCase);

	private MSBuildWorkspace? _workspace;
	private Solution? _solution;
	private readonly List<string> _workspaceFailures = [];

	public WorkspaceService(string solutionPath) => _solutionPath = solutionPath;

	/// <summary>
	/// Workspace diagnostic failures accumulated since the last OpenAsync call.
	/// These are MSBuild evaluation errors, missing SDK, unsupported project types, etc.
	/// </summary>
	public IReadOnlyList<string> WorkspaceFailures => _workspaceFailures;

	/// <summary>
	/// Returns an up-to-date Solution. On the first call the workspace is opened from disk.
	/// On subsequent calls only files whose LastWriteTimeUtc changed are re-parsed;
	/// all other compilation state is reused by Roslyn.
	/// </summary>
	public async Task<Solution> GetSolutionAsync(CancellationToken ct = default)
	{
		await _lock.WaitAsync(ct);
		try
		{
			if (_solution is null)
			{
				await OpenAsync(ct);
			}
			else
			{
				await RefreshChangedFilesAsync(ct);
			}

			return _solution!;
		}
		finally
		{
			_lock.Release();
		}
	}

	private async Task OpenAsync(CancellationToken ct)
	{
		_workspace?.Dispose();
		var hostServices = MefHostServices.Create(SMefAssemblies.Value);
		_workspaceFailures.Clear();
		_workspace = MSBuildWorkspace.Create(hostServices);
		_workspace.RegisterWorkspaceFailedHandler(e =>
		{
			var msg = $"[{e.Diagnostic.Kind}] {e.Diagnostic.Message}";
			_workspaceFailures.Add(msg);
			Console.Error.WriteLine($"[workspace] {msg}");
		});

		_solution = await _workspace.OpenSolutionAsync(_solutionPath, cancellationToken: ct);

		// Snapshot timestamps so we can detect changes on subsequent calls.
		_fileTimestamps.Clear();
		foreach (var doc in AllDocuments(_solution))
			RecordTimestamp(doc.FilePath);
	}

	/// <summary>
	/// For every document whose file changed on disk since the last snapshot,
	/// applies a WithDocumentText update. Roslyn invalidates only affected compilations.
	/// </summary>
	private async Task RefreshChangedFilesAsync(CancellationToken ct)
	{
		var updated = _solution!;

		foreach (var doc in AllDocuments(_solution!))
		{
			if (doc.FilePath is null)
			{
				continue;
			}

			var info = new FileInfo(doc.FilePath);
			if (!info.Exists)
			{
				continue;
			}

			var diskTime = info.LastWriteTimeUtc;
			if (_fileTimestamps.TryGetValue(doc.FilePath, out var cached) && cached == diskTime)
			{
				continue;
			}

			var newText = SourceText.From(await File.ReadAllTextAsync(doc.FilePath, ct));
			updated = updated.WithDocumentText(doc.Id, newText);
			_fileTimestamps[doc.FilePath] = diskTime;
		}

		_solution = updated;
	}

	private static IEnumerable<Document> AllDocuments(Solution solution) =>
		solution.Projects.SelectMany(p => p.Documents);

	private void RecordTimestamp(string? filePath)
	{
		if (filePath is null)
		{
			return;
		}

		var info = new FileInfo(filePath);
		if (info.Exists)
		{
			_fileTimestamps[filePath] = info.LastWriteTimeUtc;
		}
	}

	private static ImmutableArray<Assembly> BuildMefAssemblies()
	{
		var assemblies = MefHostServices.DefaultAssemblies;
		foreach (var name in new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
		{
			try
			{
				assemblies = assemblies.Add(Assembly.Load(name));
			}
#pragma warning disable CA1031
			catch
#pragma warning restore CA1031
			{
				// optional — code fixes degrade gracefully
			}
		}
		return assemblies;
	}

	public void Dispose()
	{
		_lock.Dispose();
		_workspace?.Dispose();
	}
}
