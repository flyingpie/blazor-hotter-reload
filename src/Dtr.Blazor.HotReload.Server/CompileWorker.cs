using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dtr.Blazor.HotReload.Server.Compiling;
using Dtr.Blazor.HotReload.Server.Deltas;

namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// Watches the app project and the libraries it ships, and turns file changes into updates for the running client:
/// <list type="number">
/// <item>Preferably an in-place update (Edit-and-Continue delta): the running app is patched, and keeps all its state.</item>
/// <item>Otherwise (e.g. "rude edits" like a changed method signature, or changes to the service registrations), a full swap:
/// a new generation of the app, compiled from scratch.</item>
/// </list>
/// All the work happens on a single loop, so changes are handled one batch at a time.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The watcher should keep running")]
internal sealed class CompileWorker : IDisposable
{
	private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
	private static readonly TimeSpan SourceDebounce = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan ScopedCssDebounce = TimeSpan.FromMilliseconds(300);

	// The build writes the intermediate assembly before copying everything to the output directory, give it some time.
	private static readonly TimeSpan BuildDebounce = TimeSpan.FromSeconds(2);

	// Our own build's file notifications can arrive after the process exits; ignore them for a moment too.
	private static readonly TimeSpan OwnBuildGracePeriod = BuildDebounce + TimeSpan.FromSeconds(1);

	private readonly ProjectLocation _location;
	private readonly HotReloadHub _hub;
	private readonly string _baselineDirectory;
	private readonly Lock _lock = new();
	private readonly SemaphoreSlim _signal = new(0);
	private readonly HashSet<string> _changedCss = new(StringComparer.Ordinal);
	private readonly HashSet<string> _changedProjects = new(StringComparer.Ordinal);
	private readonly HashSet<string> _changedFiles = new(StringComparer.Ordinal);
	private readonly List<FileSystemWatcher> _watchers = [];
	private readonly DevServerProcess? _devServer;

	private List<ProjectState> _projects = [];
	private HotSolution? _solution;
	private DeltaSession? _deltas;
	private string? _capabilities;
	private bool _assetsChanged;
	private bool _buildChanged;
	private bool _scopedCssChanged;
	private bool _buildingScopedCss;
	private bool _fullSwapRequested;
	private DateTime _ignoreBuildOutputUntil;
	private Timer? _debounce;

	public CompileWorker(ProjectLocation location, HotReloadHub hub, DevServerProcess? devServer)
	{
		_location = location;
		_hub = hub;
		_devServer = devServer;
		_baselineDirectory = Path.Combine(Path.GetDirectoryName(location.IntermediateAssembly)!, "dtr-hotreload");
		_hub.ClientMessage += OnClientMessage;
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
				build,
				scopedCss,
				fullSwap;
			string[] css,
				changedProjects,
				changedFiles;
			string? capabilities;
			lock (_lock)
			{
				(assets, build, scopedCss, fullSwap, capabilities) = (
					_assetsChanged,
					_buildChanged,
					_scopedCssChanged,
					_fullSwapRequested,
					_capabilities
				);
				css = [.. _changedCss];
				changedProjects = [.. _changedProjects];
				changedFiles = [.. _changedFiles];
				_assetsChanged = _buildChanged = _scopedCssChanged = _fullSwapRequested = false;
				_changedCss.Clear();
				_changedProjects.Clear();
				_changedFiles.Clear();
			}

			try
			{
				if (capabilities is not null && _deltas is { HasCapabilities: false })
				{
					_deltas.SetCapabilities(capabilities);
				}

				// Scoped CSS needs a real build, which also compiles any source changes in this batch.
				if (scopedCss)
				{
					await BuildScopedCssAsync(token).ConfigureAwait(false);
					continue;
				}

				if (build)
				{
					await OnBuildAsync(token).ConfigureAwait(false);
					continue;
				}

				if (changedProjects.Length > 0 || fullSwap)
				{
					await CompileAsync(changedProjects, changedFiles, fullSwap).ConfigureAwait(false);
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
		_hub.ClientMessage -= OnClientMessage;
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
	/// Changes to the service registrations can be applied in place, but that doesn't rebuild the DI container, so they need a full swap.
	/// </summary>
	private static bool TouchesServiceRegistrations(IEnumerable<string> files) =>
		files.Any(file =>
			Path.GetExtension(file) == ".cs"
			&& File.Exists(file)
			&& File.ReadAllText(file).Contains("IServiceCollection", StringComparison.Ordinal)
		);

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

	/// <summary>
	/// Scoped CSS is generated at build time (the <c>b-...</c> attributes and the bundled stylesheet), so an in-memory compile
	/// can't apply it. Build the app, then start over from the new output. The build's own output events are ignored: this
	/// method handles that output itself.
	/// </summary>
	private async Task BuildScopedCssAsync(CancellationToken token)
	{
		Log.Info("Scoped CSS changed, building the app...");
		_hub.Broadcast(new { type = "compiling" });

		lock (_lock)
		{
			_buildingScopedCss = true;
		}

		try
		{
			// A desktop app keeps its output files open (on Windows), so stop it before the build replaces them.
			_devServer?.Stop();

			var (exitCode, _) = await DotnetCli
				.RunAsync(["build", _location.ProjectFile, "-c", "Debug", "--nologo", "--no-restore"], echo: true)
				.ConfigureAwait(false);
			if (exitCode != 0)
			{
				Log.Error("Build failed, the app keeps running the previous version.");

				_hub.Broadcast(
					new
					{
						type = "errors",
						diagnostics = new[]
						{
							new CompileDiagnostic(
								Path.GetFileName(_location.ProjectFile),
								1,
								1,
								"BUILD",
								"Scoped CSS build failed, see the server console."
							),
						},
					}
				);

				// We stopped it above, so bring the previous version back.
				if (_devServer is not null)
				{
					await _devServer.StartAsync(token).ConfigureAwait(false);
				}

				return;
			}
		}
		finally
		{
			lock (_lock)
			{
				_buildingScopedCss = false;
				_buildChanged = false;
				_ignoreBuildOutputUntil = DateTime.UtcNow + OwnBuildGracePeriod;
			}
		}

		await OnBuildAsync(token).ConfigureAwait(false);
	}

	/// <summary>
	/// (Re)builds the project graph, the Roslyn workspace, and the delta session from the current build output.
	/// </summary>
	private async Task LoadProjectsAsync()
	{
		var stopwatch = Stopwatch.StartNew();
		var projects = await ProjectGraph.LoadAsync(_location).ConfigureAwait(false);
		Log.Info($"Projects: {string.Join(", ", projects.Select(p => p.Name))} ({stopwatch.ElapsedMilliseconds} ms)");

		// Baseline copies of generations are only valid for the build output they were created from.
		if (Directory.Exists(_baselineDirectory))
		{
			Directory.Delete(_baselineDirectory, recursive: true);
		}

		var solution = HotSolution.Create(projects, _location.IntermediateAssembly);
		await solution.WarmUpAsync().ConfigureAwait(false);

		// The client runs the build output, which is the baseline for the in-place updates.
		var deltas = DeltaSession.TryCreate(solution.Solution.Services);
		if (deltas is not null)
		{
			if (_capabilities is not null)
			{
				deltas.SetCapabilities(_capabilities);
			}

			await deltas.StartAsync(solution.Solution).ConfigureAwait(false);
		}

		lock (_lock)
		{
			_projects = [.. projects.Select(p => new ProjectState(p))];
			_solution = solution;
			_deltas = deltas;
		}
	}

	/// <summary>
	/// Handles a source change: try an in-place update first, and fall back to a full swap when the delta engine can't apply
	/// it (or when the change touches service registrations).
	/// </summary>
	private async Task CompileAsync(IReadOnlyCollection<string> changedProjects, IReadOnlyCollection<string> changedFiles, bool fullSwap)
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

		if (!fullSwap && _deltas is { HasCapabilities: true } && !TouchesServiceRegistrations(changedFiles))
		{
			_hub.Broadcast(new { type = "compiling" });
			var result = await _deltas.GetUpdatesAsync(_solution.Solution, _solution.ProjectIds).ConfigureAwait(false);
			switch (result.Status)
			{
				case DeltaStatus.NoChanges:
					Log.Info($"No changes to apply ({stopwatch.ElapsedMilliseconds} ms)");
					_hub.Broadcast(new { type = "nochanges" });
					return;

				case DeltaStatus.Ready:
					_deltas.Commit();
					var sequence = _hub.PublishDelta(result.Updates);
					foreach (var update in result.Updates)
					{
						_projects.Single(p => p.Project == _solution.GetProject(update.ProjectId)).ChangedSinceBuild = true;
					}

					changed.ForEach(p => p.HasErrors = false);
					var names = string.Join(", ", result.Updates.Select(u => _solution.GetProject(u.ProjectId).Name));
					Log.Success(
						$"Update {_hub.Generation}.{sequence} applied in place ({names}) in {stopwatch.ElapsedMilliseconds} ms, sent to {_hub.ClientCount} client(s)"
					);
					return;

				case DeltaStatus.CompileErrors:
					// Either the code doesn't compile, or the delta can't be emitted for this runtime (e.g. CS9346: an update that needs
					// an explicit interface implementation, which the WebAssembly runtime can't add). The full compile tells which:
					// it reports the errors in the first case, and swaps the app in the second.
					var errors = result.Diagnostics.Where(DeltaSession.IsCompileError).Select(d => $"{d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}");
					Log.Info($"Can't be applied in place, compiling the whole app: {string.Join("; ", errors.Distinct().Take(3))}");
					break;

				default:
					var rudeEdits = result
						.Diagnostics.Where(d => d.Id.StartsWith("ENC", StringComparison.Ordinal))
						.Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
					Log.Info($"Can't be applied in place, restarting the app: {string.Join("; ", rudeEdits.Distinct().Take(3))}");
					break;
			}
		}

		await FullSwapAsync(changed, stopwatch).ConfigureAwait(false);
	}

	/// <summary>
	/// Compiles a new generation of the app, plus the libraries that changed since the last build (and their dependents), and publishes it.
	/// </summary>
	private async Task FullSwapAsync(List<ProjectState> changed, Stopwatch stopwatch)
	{
		_hub.Broadcast(new { type = "compiling" });

		// Shipped: the app, and every library whose code differs from the build output (including in-place updates), plus libraries that
		// depend on those (so they bind to the new versions too). _projects is in dependency order, so one pass is enough.
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

		// What the client now runs, is the baseline for the next in-place updates. Write the exact bytes that were sent to
		// disk; the delta engine needs the matching PDB to compute updates against this generation.
		if (_deltas is not null)
		{
			var directory = Path.Combine(_baselineDirectory, generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
			Directory.CreateDirectory(directory);
			foreach (var project in shipped)
			{
				var result = results[project.Project.AssemblyName];
				var assemblyPath = Path.Combine(directory, project.Project.AssemblyName + ".dll");
				await File.WriteAllBytesAsync(assemblyPath, result.Assembly!).ConfigureAwait(false);
				await File.WriteAllBytesAsync(Path.ChangeExtension(assemblyPath, ".pdb"), result.Symbols!).ConfigureAwait(false);
				_solution!.SetBaseline(project.Project, assemblyPath);
			}

			await _deltas.StartAsync(_solution!.Solution).ConfigureAwait(false);
		}

		Log.Success(
			$"Generation {generation} compiled in {stopwatch.ElapsedMilliseconds} ms ({string.Join(", ", shipped.Select(p => p.Project.Name))}), "
				+ $"app restarted in {_hub.ClientCount} client(s)"
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
	/// Handles the two kinds of client messages that affect compilation: what the runtime can apply, and requests for a full
	/// swap. The caller signals the worker loop after a request.
	/// </summary>
	private void OnClientMessage(string type, JsonElement message)
	{
		lock (_lock)
		{
			switch (type)
			{
				case "capabilities":
					// The first client that reports capabilities sets them; all clients in one app share the runtime, but a
					// reloaded page may connect before the old one disconnects.
					var supported = message.TryGetProperty("supported", out var s) && s.GetBoolean();
					var capabilities = message.TryGetProperty("capabilities", out var c) ? c.GetString() : null;
					Log.Info($"Client supports in-place updates: {supported}{(supported ? $" ({capabilities})" : string.Empty)}");
					if (!supported || string.IsNullOrWhiteSpace(capabilities) || _capabilities is not null)
					{
						return;
					}

					_capabilities = capabilities;
					break;

				case "delta-failed":
				case "restart":
					Log.Warning(
						type == "restart"
							? "Restart requested by the client"
							: "The client could not apply an in-place update, restarting the app"
					);
					_fullSwapRequested = true;
					break;

				default:
					return;
			}
		}

		_signal.Release();
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

		// Every hot project's assembly: a regular build of the app also rebuilds the libraries, and the app assembly itself might not
		// change (e.g. when only a library changed). Plus the WebAssembly boot config, which also changes when only static assets changed.
		var outputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		void AddOutput(string directory, string fileName)
		{
			if (!outputs.TryGetValue(directory, out var files))
			{
				outputs[directory] = files = new HashSet<string>(StringComparer.Ordinal);
			}

			files.Add(fileName);
		}

		foreach (var project in _projects)
		{
			AddOutput(Path.GetDirectoryName(project.Project.ResponseFile)!, project.Project.AssemblyName + ".dll");
		}

		AddOutput(Path.GetDirectoryName(_location.IntermediateAssembly)!, "dotnet.js");

		foreach (var (directory, files) in outputs)
		{
			var buildOutput = new FileSystemWatcher(directory)
			{
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			FileSystemEventHandler onChanged = (_, e) =>
			{
				if (e.Name is not null && files.Contains(e.Name))
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
	}

	/// <summary>
	/// Classifies a file change: CSS and other wwwroot assets are handled separately from source changes; project files and
	/// scoped CSS need a regular build (which cannot be done by the in-memory compiler).
	/// </summary>
	private void OnSourceChanged(string projectDirectory, string path)
	{
		if (IsIgnored(projectDirectory, path))
		{
			return;
		}

		var scopedCss = IsScopedCss(path);

		lock (_lock)
		{
			var project = _projects.FirstOrDefault(p => p.Project.ProjectDirectory == projectDirectory);
			if (project is null)
			{
				return;
			}

			var extension = Path.GetExtension(path);
			var wwwroot = Path.Combine(projectDirectory, "wwwroot");
			if (scopedCss)
			{
				_scopedCssChanged = true;
			}
			else if (path.StartsWith(wwwroot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
			{
				if (extension == ".css")
				{
					_changedCss.Add(Path.GetRelativePath(wwwroot, path).Replace('\\', '/'));
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
				_changedFiles.Add(path);
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

		Debounce(scopedCss ? ScopedCssDebounce : SourceDebounce);
	}

	/// <summary>
	/// A scoped CSS file (<c>Component.razor.css</c> or <c>Page.cshtml.css</c>). The SDK generates the scoped attributes and
	/// bundled stylesheet at build time, so the watcher treats it as a build request, not as a source file.
	/// </summary>
	private static bool IsScopedCss(string path)
	{
		var name = Path.GetFileName(path);
		return name.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".cshtml.css", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A regular build wrote a new assembly/boot config. Give the build time to finish copying all output files, then reset
	/// from the new build. Our own scoped CSS build is handled by <see cref="BuildScopedCssAsync"/> instead.
	/// </summary>
	private void OnBuildOutputChanged()
	{
		lock (_lock)
		{
			if (_buildingScopedCss || DateTime.UtcNow < _ignoreBuildOutputUntil)
			{
				return;
			}

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
		/// Whether the client runs a different version of this project than the build output (through in-place updates or a full swap).
		/// </summary>
		public bool ChangedSinceBuild { get; set; }
	}
}
