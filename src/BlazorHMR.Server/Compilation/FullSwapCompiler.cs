using System.Diagnostics;
using System.Globalization;
using BlazorHMR.Protocol;
using BlazorHMR.Server.Project;
using BlazorHMR.Server.Server;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// Compiles a complete new generation of the app: the app itself, plus the libraries that differ from the build output
/// (including in-place patches), plus the libraries that depend on those. Publishes it and stores it as the baseline for the
/// next in-place patches.
/// </summary>
internal sealed class FullSwapCompiler(HotReloadHub hub, string baselineDirectory)
{
	/// <summary>
	/// Compiles and publishes a generation. Returns false when a project doesn't compile; the previous generation keeps running.
	/// </summary>
	public async Task<bool> CompileAndPublishAsync(
		HotSolution solution,
		IReadOnlyList<ProjectState> projects,
		IReadOnlyCollection<ProjectState> changed,
		bool storeBaseline,
		Func<Task> restartInPlaceBaselineAsync
	)
	{
		ArgumentNullException.ThrowIfNull(solution);
		ArgumentNullException.ThrowIfNull(projects);
		ArgumentNullException.ThrowIfNull(changed);
		ArgumentNullException.ThrowIfNull(restartInPlaceBaselineAsync);

		var stopwatch = Stopwatch.StartNew();
		hub.Broadcast(new CompilingMessage());

		// Shipped: the app, and every library whose code differs from the build output (including in-place patches), plus libraries that
		// depend on those (so they bind to the new versions too). projects is in dependency order, so one pass is enough.
		var shipped = new List<ProjectState>();
		foreach (var project in projects)
		{
			if (
				project.Project.IsApp
				|| project.ChangedSinceBuild
				|| changed.Contains(project)
				|| shipped.Exists(s => project.Project.Dependencies.Contains(s.Project.ProjectFile))
			)
			{
				shipped.Add(project);
			}
		}

		var results = new Dictionary<string, CompileResult>(StringComparer.OrdinalIgnoreCase);
		foreach (var project in shipped)
		{
			var result = await solution.EmitAsync(project.Project).ConfigureAwait(false);
			if (!result.Success)
			{
				project.HasErrors = true;
				ReportErrors([.. result.Errors.Select(e => e with { File = $"{project.Project.Name}: {e.File}" })]);
				return false;
			}

			project.HasErrors = false;
			results[project.Project.AssemblyName] = result;
		}

		foreach (var project in shipped.Where(p => !p.Project.IsApp))
		{
			project.ChangedSinceBuild = true;
		}

		foreach (var project in changed)
		{
			project.HasErrors = false;
		}

		var generation = hub.Publish(results);

		// What the client now runs, is the baseline for the next in-place patches. Write the exact bytes that were sent to
		// disk; the delta engine needs the matching PDB to compute updates against this generation.
		if (storeBaseline)
		{
			var directory = Path.Combine(baselineDirectory, generation.ToString(CultureInfo.InvariantCulture));
			Directory.CreateDirectory(directory);
			foreach (var project in shipped)
			{
				var result = results[project.Project.AssemblyName];
				var assemblyPath = Path.Combine(directory, project.Project.AssemblyName + ".dll");
				await File.WriteAllBytesAsync(assemblyPath, result.Assembly!).ConfigureAwait(false);
				await File.WriteAllBytesAsync(Path.ChangeExtension(assemblyPath, ".pdb"), result.Symbols!).ConfigureAwait(false);
				solution.SetBaseline(project.Project, assemblyPath);
			}

			await restartInPlaceBaselineAsync().ConfigureAwait(false);
		}

		stopwatch.Stop();
		Log.Success(
			$"Generation {generation} compiled in {stopwatch.ElapsedMilliseconds} ms ({string.Join(", ", shipped.Select(p => p.Project.Name))}), "
				+ $"app restarted in {hub.ClientCount} client(s)"
		);
		return true;
	}

	private void ReportErrors(List<CompileDiagnostic> errors)
	{
		Log.Error("Compilation failed:");
		foreach (var error in errors)
		{
			Log.Error($"  {error.File}({error.Line},{error.Column}): {error.Id}: {error.Message}");
		}

		hub.Broadcast(new ErrorsMessage(errors));
	}
}
