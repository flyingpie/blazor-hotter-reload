using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Dtr.Blazor.HotReload.Server.Compiling;

namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// Watches the app project and the libraries it ships to the browser, and turns file changes into updates for the browser:
/// a new generation of the app, compiled from scratch.
/// All the work happens on a single loop, so changes are handled one batch at a time.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The watcher should keep running")]
internal sealed class CompileWorker : IDisposable
{
	private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
	private static readonly TimeSpan SourceDebounce = TimeSpan.FromMilliseconds(100);

	// The build writes the intermediate assembly before copying everything to the output directory, give it some time.
	private static readonly TimeSpan BuildDebounce = TimeSpan.FromSeconds(2);

	private readonly ProjectLocation _location;
	private readonly HotReloadHub _hub;
	private readonly string _wwwroot;
	private readonly Lock _lock = new();
	private readonly SemaphoreSlim _signal = new(0);
	private readonly HashSet<string> _changedCss = new(StringComparer.Ordinal);
	private readonly HashSet<string> _changedProjects = new(StringComparer.Ordinal);
	private readonly List<FileSystemWatcher> _watchers = [];
	private readonly DevServerProcess? _devServer;

	private List<ProjectState> _projects = [];
	private HotSolution? _solution;
	private bool _assetsChanged;
	private bool _buildChanged;
	private Timer? _debounce;

	public CompileWorker(ProjectLocation location, HotReloadHub hub, DevServerProcess? devServer)
	{
		_location = location;
		_hub = hub;
		_devServer = devServer;
		_wwwroot = Path.Combine(location.ProjectDirectory, "wwwroot");
	}

	/// <summary>
	/// The single worker loop: wait for a debounced signal, take a snapshot of everything that changed, handle it, repeat.
	/// </summary>
	public async Task RunAsync(CancellationToken token)
	{
		await LoadProjectsAsync().ConfigureAwait(false);
		StartWatching();

		while (!token.IsCancellationRequested)
		{
			await _signal.WaitAsync(token).ConfigureAwait(false);

			// The file watchers and the WebSocket handlers add to the changed sets under _lock. Take a snapshot and reset
			// the sets, so changes that arrive while we compile are handled in the next iteration.
			bool assets,
				build;
			string[] css,
				changedProjects;
			lock (_lock)
			{
				(assets, build) = (_assetsChanged, _buildChanged);
				css = [.. _changedCss];
				changedProjects = [.. _changedProjects];
				_assetsChanged = _buildChanged = false;
				_changedCss.Clear();
				_changedProjects.Clear();
			}

			try
			{
				if (build)
				{
					await OnBuildAsync(token).ConfigureAwait(false);
					continue;
				}

				if (changedProjects.Length > 0)
				{
					await CompileAsync(changedProjects).ConfigureAwait(false);
				}

				foreach (var path in css)
				{
					Log.Info($"CSS changed: {path}");
					_hub.PublishCss(path);
				}

				if (assets)
				{
					Log.Info("Static asset changed, reloading the page");
					_hub.Broadcast(new { type = "reload" });
				}
			}
			catch (Exception ex)
			{
				Log.Error($"Hot reload failed: {ex}");
			}
		}
	}

	public void Dispose()
	{
		_watchers.ForEach(w => w.Dispose());
		_debounce?.Dispose();
		_signal.Dispose();
	}

	/// <summary>
	/// Changes under bin/obj, node_modules, or hidden directories are not source changes, and neither are the temporary files
	/// that editors create while saving.
	/// </summary>
	private static bool IsIgnored(string projectDirectory, string path)
	{
		var relative = Path.GetRelativePath(projectDirectory, path);
		var parts = relative.Split(PathSeparators);
		if (parts[0] is "bin" or "obj" or "node_modules" || parts.Any(p => p.StartsWith('.')))
		{
			return true;
		}

		// Temporary files of editors (vim, JetBrains, VS, emacs, ...).
		var name = parts[^1];
		return name.EndsWith('~')
			|| name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".swp", StringComparison.Ordinal)
			|| name.Contains("___jb_", StringComparison.Ordinal)
			|| name.StartsWith('~');
	}

	/// <summary>
	/// A regular build of the app happened (which also builds the libraries): start over from its output.
	/// </summary>
	private async Task OnBuildAsync(CancellationToken token)
	{
		Log.Info("Build output changed, starting over from the new build");
		await LoadProjectsAsync().ConfigureAwait(false);
		_hub.Reset();

		// The dev server only picks up the new (fingerprinted) build output after a restart.
		if (_devServer is null)
		{
			_hub.Broadcast(new { type = "rebuilt" });
		}
		else
		{
			await _devServer.RestartAsync(token).ConfigureAwait(false);
			_hub.Broadcast(new { type = "reload" });
		}
	}

	private async Task LoadProjectsAsync()
	{
		var stopwatch = Stopwatch.StartNew();
		var projects = await ProjectGraph.LoadAsync(_location).ConfigureAwait(false);
		Log.Info($"Projects: {string.Join(", ", projects.Select(p => p.Name))} ({stopwatch.ElapsedMilliseconds} ms)");

		var solution = HotSolution.Create(projects);
		await solution.WarmUpAsync().ConfigureAwait(false);

		lock (_lock)
		{
			_projects = [.. projects.Select(p => new ProjectState(p))];
			_solution = solution;
		}
	}

	private async Task CompileAsync(IReadOnlyCollection<string> changedProjects)
	{
		var stopwatch = Stopwatch.StartNew();

		// Projects with errors from last time are included, so their dependents get compiled once they're fixed.
		var changed = _projects.Where(p => changedProjects.Contains(p.Project.ProjectFile) || p.HasErrors).ToList();
		await _solution!.SyncAsync(changed.Select(p => p.Project)).ConfigureAwait(false);

		// Even if this doesn't compile (e.g. a library change that breaks the app), the library has to be shipped once it does.
		foreach (var project in changed.Where(p => !p.Project.IsApp))
		{
			project.ChangedSinceBuild = true;
		}

		await FullSwapAsync(changed, stopwatch).ConfigureAwait(false);
	}

	/// <summary>
	/// Compiles a new generation of the app, plus the libraries that changed since the last build (and their dependents), and publishes it.
	/// </summary>
	private async Task FullSwapAsync(List<ProjectState> changed, Stopwatch stopwatch)
	{
		_hub.Broadcast(new { type = "compiling" });

		// Shipped: the app, and every library whose code differs from the build output, plus libraries that depend on those
		// (so they bind to the new versions too). _projects is in dependency order, so one pass is enough.
		var shipped = new List<ProjectState>();
		foreach (var project in _projects)
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
			var result = await _solution!.EmitAsync(project.Project).ConfigureAwait(false);
			if (!result.Success)
			{
				project.HasErrors = true;
				ReportErrors([.. result.Errors.Select(e => e with { File = $"{project.Project.Name}: {e.File}" })]);
				return;
			}

			project.HasErrors = false;
			results[project.Project.AssemblyName] = result;
		}

		foreach (var project in shipped.Where(p => !p.Project.IsApp))
		{
			project.ChangedSinceBuild = true;
		}

		changed.ForEach(p => p.HasErrors = false);
		var generation = _hub.Publish(results);

		Log.Success(
			$"Generation {generation} compiled in {stopwatch.ElapsedMilliseconds} ms ({string.Join(", ", shipped.Select(p => p.Project.Name))}), "
				+ $"app restarted in {_hub.ClientCount} browser(s)"
		);
	}

	private void ReportErrors(List<CompileDiagnostic> errors)
	{
		Log.Error("Compilation failed:");
		foreach (var error in errors)
		{
			Log.Error($"  {error.File}({error.Line},{error.Column}): {error.Id}: {error.Message}");
		}

		_hub.Broadcast(new { type = "errors", diagnostics = errors });
	}

	/// <summary>
	/// Watches two things: source files (for hot reload), and the build output (for a regular `dotnet build` that the user
	/// ran themselves, which requires starting over from the new output).
	/// </summary>
	private void StartWatching()
	{
		foreach (var directory in _projects.Select(p => p.Project.ProjectDirectory))
		{
			var sources = new FileSystemWatcher(directory)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			sources.Changed += (_, e) => OnSourceChanged(directory, e.FullPath);
			sources.Created += (_, e) => OnSourceChanged(directory, e.FullPath);
			sources.Deleted += (_, e) => OnSourceChanged(directory, e.FullPath);
			sources.Renamed += (_, e) =>
			{
				OnSourceChanged(directory, e.OldFullPath);
				OnSourceChanged(directory, e.FullPath);
			};
			sources.Error += (_, e) => Log.Warning($"File watcher error: {e.GetException().Message}");
			sources.EnableRaisingEvents = true;
			_watchers.Add(sources);
			Log.Success($"Watching {directory}");
		}

		// The app assembly, and the boot config (which also changes when only static assets changed, e.g. in a library).
		string[] buildOutputs = [Path.GetFileName(_location.IntermediateAssembly), "dotnet.js"];
		var buildOutput = new FileSystemWatcher(Path.GetDirectoryName(_location.IntermediateAssembly)!)
		{
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
		};
		FileSystemEventHandler onChanged = (_, e) =>
		{
			if (buildOutputs.Contains(e.Name, StringComparer.Ordinal))
			{
				OnBuildOutputChanged();
			}
		};
		buildOutput.Changed += onChanged;
		buildOutput.Created += onChanged;
		buildOutput.Renamed += (sender, e) => onChanged(sender, e);
		buildOutput.EnableRaisingEvents = true;
		_watchers.Add(buildOutput);
	}

	/// <summary>
	/// Classifies a file change: CSS and other wwwroot assets are handled separately from source changes, and project files
	/// need a regular build (which cannot be done by the in-memory compiler).
	/// </summary>
	private void OnSourceChanged(string projectDirectory, string path)
	{
		if (IsIgnored(projectDirectory, path))
		{
			return;
		}

		lock (_lock)
		{
			var project = _projects.FirstOrDefault(p => p.Project.ProjectDirectory == projectDirectory);
			if (project is null)
			{
				return;
			}

			var extension = Path.GetExtension(path);
			if (project.Project.IsApp && path.StartsWith(_wwwroot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
			{
				if (extension == ".css")
				{
					_changedCss.Add(Path.GetRelativePath(_wwwroot, path).Replace('\\', '/'));
				}
				else if (!Directory.Exists(path))
				{
					_assetsChanged = true;
				}
			}
			else if (ProjectInputs.IsLiveFile(projectDirectory, path) || Directory.Exists(path) || extension.Length == 0)
			{
				// Also directories: a deleted/renamed directory takes its files with it.
				_changedProjects.Add(project.Project.ProjectFile);
			}
			else if (extension is ".csproj" or ".props" or ".targets")
			{
				Log.Warning($"{Path.GetFileName(path)} changed, this needs a regular build (which hot reload will pick up)");
				return;
			}
			else
			{
				return;
			}
		}

		Debounce(SourceDebounce);
	}

	/// <summary>
	/// A regular build wrote a new assembly/boot config. Give the build time to finish copying all output files, then reset
	/// from the new build.
	/// </summary>
	private void OnBuildOutputChanged()
	{
		lock (_lock)
		{
			_buildChanged = true;
		}

		Debounce(BuildDebounce);
	}

	/// <summary>
	/// Coalesces a burst of file events into one signal. The timer is reused; changing its due time replaces the pending one.
	/// </summary>
	private void Debounce(TimeSpan delay)
	{
		lock (_lock)
		{
			_debounce ??= new Timer(_ => _signal.Release());
			_debounce.Change(delay, Timeout.InfiniteTimeSpan);
		}
	}

	private sealed class ProjectState(HotProject project)
	{
		public HotProject Project { get; } = project;

		public bool HasErrors { get; set; }

		/// <summary>
		/// Whether the browser runs a different version of this project than the build output.
		/// </summary>
		public bool ChangedSinceBuild { get; set; }
	}
}
