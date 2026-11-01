using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BlazorHMR.Protocol;
using BlazorHMR.Server.InPlace;
using BlazorHMR.Server.Project;
using BlazorHMR.Server.Server;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// Watches the app project and the libraries it ships, and turns file changes into updates for the running client:
/// <list type="number">
/// <item>Preferably an in-place patch (Edit-and-Continue): the running app is patched, and keeps all its state.</item>
/// <item>Otherwise (e.g. "rude edits" like a changed method signature, or changes to the service registrations), a full swap:
/// a new generation of the app, compiled from scratch.</item>
/// </list>
/// All the work happens on a single loop, so changes are handled one batch at a time. The watchers, change classification,
/// and real builds live in their own types; this loop only drains a batch and calls the matching compiler.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The watcher should keep running")]
internal sealed class CompileWorker : IDisposable
{
	private readonly ProjectLocation _location;
	private readonly HotReloadHub _hub;
	private readonly string _baselineDirectory;
	private readonly InPlacePatchCompiler _inPlace = new();
	private readonly FullSwapCompiler _fullSwap;
	private readonly ChangeClassifier _changes = new();
	private readonly SourceWatcher _watcher;
	private readonly BuildRunner _builds;
	private readonly Lock _lock = new();
	private readonly SemaphoreSlim _signal = new(0);

	private List<ProjectState> _projects = [];
	private HotSolution? _solution;
	private string? _capabilities;

	public CompileWorker(ProjectLocation location, HotReloadHub hub, DevServerProcess? devServer)
	{
		_location = location;
		_hub = hub;
		_baselineDirectory = Path.Combine(Path.GetDirectoryName(location.IntermediateAssembly)!, "blazor-hmr");
		_fullSwap = new FullSwapCompiler(hub, _baselineDirectory);
		_builds = new BuildRunner(location, hub, _changes, devServer);
		_watcher = new SourceWatcher(_changes, () => _signal.Release());
		_hub.ClientMessage += OnClientMessage;
	}

	/// <summary>
	/// The single worker loop: wait for a debounced signal, take a snapshot of everything that changed, handle it, repeat.
	/// </summary>
	public async Task RunAsync(CancellationToken token)
	{
		await LoadProjectsAsync().ConfigureAwait(false);
		_watcher.Start(_projects);

		while (!token.IsCancellationRequested)
		{
			await _signal.WaitAsync(token).ConfigureAwait(false);

			var change = _changes.Take();
			try
			{
				if (Capabilities is { } capabilities && _inPlace.IsAvailable && !_inPlace.HasCapabilities)
				{
					_inPlace.SetCapabilities(capabilities);
				}

				// Scoped CSS needs a real build, which also compiles any source changes in this batch.
				if (change.ScopedCss)
				{
					await _builds.RebuildForScopedCssAsync(LoadProjectsAsync, token).ConfigureAwait(false);
					continue;
				}

				if (change.Build)
				{
					await _builds.RestartFromBuildAsync(LoadProjectsAsync, token).ConfigureAwait(false);
					continue;
				}

				if (change.Projects.Length > 0 || change.FullSwap)
				{
					await CompileAsync(change.Projects, change.Files, change.FullSwap).ConfigureAwait(false);
				}

				foreach (var path in change.Css)
				{
					Log.Info($"CSS changed: {path}");
					_hub.PublishCss(path);
				}

				if (change.Assets)
				{
					Log.Info("Static asset changed, reloading the page");
					_hub.Broadcast(new ReloadMessage());
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
		_watcher.Dispose();
		_signal.Dispose();
	}

	/// <summary>
	/// What the client's runtime reported it can apply, if it has reported yet.
	/// </summary>
	private string? Capabilities
	{
		get
		{
			lock (_lock)
			{
				return _capabilities;
			}
		}
	}

	/// <summary>
	/// (Re)builds the project graph, the Roslyn workspace, and the in-place patch session from the current build output.
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

		// The client runs the build output, which is the baseline for the in-place patches.
		await _inPlace.ResetAsync(solution, Capabilities).ConfigureAwait(false);

		lock (_lock)
		{
			_projects = [.. projects.Select(p => new ProjectState(p))];
			_solution = solution;
		}
	}

	/// <summary>
	/// Handles a source change: try the in-place pipeline first, and fall back to a full swap when it can't apply the change
	/// (or when the change touches service registrations).
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

		if (PatchPlanner.ShouldTryInPlace(fullSwap, _inPlace.HasCapabilities, changedFiles))
		{
			_hub.Broadcast(new CompilingMessage());
			var result = await _inPlace.CompileAsync(_solution.Solution, _solution.ProjectIds).ConfigureAwait(false);
			switch (result.Status)
			{
				case PatchStatus.NoChanges:
					Log.Info($"No changes to apply ({stopwatch.ElapsedMilliseconds} ms)");
					_hub.Broadcast(new NoChangesMessage());
					return;

				case PatchStatus.Ready:
					var sequence = _hub.PublishPatch(result.Changes);
					foreach (var change in result.Changes)
					{
						_projects.Single(p => p.Project == _solution.GetProject(change.ProjectId)).ChangedSinceBuild = true;
					}

					changed.ForEach(p => p.HasErrors = false);
					var names = string.Join(", ", result.Changes.Select(c => _solution.GetProject(c.ProjectId).Name));
					Log.Success(
						$"Patch {_hub.Generation}.{sequence} applied in place ({names}) in {stopwatch.ElapsedMilliseconds} ms, sent to {_hub.ClientCount} client(s)"
					);
					return;

				case PatchStatus.CompileErrors:
					// Either the code doesn't compile, or the patch can't be emitted for this runtime (e.g. CS9346: a change that needs
					// an explicit interface implementation). The full compile tells which: it reports the errors in the first case, and
					// swaps the app in the second.
					var errors = result
						.Diagnostics.Where(PatchSession.IsCompileError)
						.Select(d => $"{d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}");
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

		await _fullSwap
			.CompileAndPublishAsync(_solution, _projects, changed, _inPlace.IsAvailable, () => _inPlace.StartAsync(_solution.Solution))
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Handles the two kinds of client messages that affect compilation: what the runtime can apply, and requests for a full
	/// swap. The caller signals the worker loop after a request.
	/// </summary>
	private void OnClientMessage(ClientMessage message)
	{
		switch (message)
		{
			case CapabilitiesMessage capabilities:
				if (!AcceptCapabilities(capabilities))
				{
					return;
				}

				break;

			case RestartMessage:
				Log.Warning("Restart requested by the client");
				_changes.RequestFullSwap();
				break;

			case PatchFailedMessage:
				Log.Warning("The client could not apply an in-place patch, restarting the app");
				_changes.RequestFullSwap();
				break;

			default:
				return;
		}

		_signal.Release();
	}

	/// <summary>
	/// Records what the runtime can apply. Returns whether this was new information (and the worker should wake up).
	/// </summary>
	private bool AcceptCapabilities(CapabilitiesMessage capabilities)
	{
		if (capabilities.ProtocolVersion != ProtocolVersion.Current)
		{
			Log.Warning(
				$"Protocol mismatch: the client speaks version {capabilities.ProtocolVersion}, the server version {ProtocolVersion.Current}"
			);
		}

		lock (_lock)
		{
			// The first client that reports capabilities sets them; all clients in one app share the runtime, but a
			// reloaded page may connect before the old one disconnects.
			Log.Info(
				$"Client supports in-place patches: {capabilities.Supported}"
					+ $"{(capabilities.Supported ? $" ({capabilities.Capabilities})" : string.Empty)}"
			);
			if (!capabilities.Supported || string.IsNullOrWhiteSpace(capabilities.Capabilities) || _capabilities is not null)
			{
				return false;
			}

			_capabilities = capabilities.Capabilities;
			return true;
		}
	}
}
