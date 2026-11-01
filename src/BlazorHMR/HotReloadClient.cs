using BlazorHMR.InPlace;
using BlazorHMR.Protocol;

namespace BlazorHMR;

/// <summary>
/// Routes what the hot reload server sends to the matching pipeline: <see cref="HotReloadRuntime.InPlace"/> for in-place
/// patches, or <see cref="HotReloadRuntime.FullSwap"/> for complete new generations. The transport, sequence tracking,
/// generation download, and page reloads live in their own types.
/// </summary>
public sealed class HotReloadClient : IAsyncDisposable
{
	private readonly HotReloadRuntime _runtime;
	private readonly GenerationFetcher _fetcher;
	private readonly PageReloadCoordinator _page;
	private readonly PatchSequence _sequence = new();
	private readonly HotReloadConnection _connection;

	private bool _freshStartRequested;

	public HotReloadClient(HotReloadRuntime runtime, IServiceProvider hostServices)
	{
		_runtime = runtime;
		var serverUrl = HotReloadOptions.ServerUrl;
		_fetcher = new GenerationFetcher(serverUrl);
		_page = new PageReloadCoordinator(hostServices);
		_connection = new HotReloadConnection(serverUrl, HandleAsync, SendCapabilitiesAsync);
	}

	/// <summary>
	/// Whether this page runs anything else than the build output.
	/// </summary>
	private bool IsModified => _sequence.IsModified || _runtime.Current.Number > 0;

	/// <summary>
	/// Starts the connection loop in the background. It reconnects forever (until disposed); the runtime never awaits it.
	/// </summary>
	public void Start() => _connection.Start();

	/// <summary>
	/// A desktop page reload keeps this process (and this connection) alive, but the page's services and JS runtime are new.
	/// </summary>
	public void Reattach(IServiceProvider hostServices) => _page.Reattach(hostServices);

	/// <summary>
	/// Asks the server for a full swap (a new generation, compiled from scratch).
	/// </summary>
	public Task RequestRestartAsync() => _connection.SendAsync(new RestartMessage());

	/// <summary>
	/// For when the page got into a weird state: asks the server for a full swap, and reloads the page once it's there (instead of
	/// swapping it in), so everything starts over (DOM, JS, state, no old generations), running the latest code.
	/// Without a connection to the server, just reloads the page.
	/// </summary>
	public async Task RequestFreshStartAsync()
	{
		if (!_connection.IsOpen)
		{
			await ReloadPageAsync().ConfigureAwait(true);
			return;
		}

		Console.WriteLine("[hotreload] Fresh start requested");
		_freshStartRequested = true;
		_runtime.SetStatus(new HotReloadStatus(HotReloadStatusKind.Compiling, "Hot reload: compiling a fresh version...", []));
		await RequestRestartAsync().ConfigureAwait(true);
	}

	/// <summary>
	/// Stops the connection, then the HTTP client, so neither is used after disposal.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		await _connection.DisposeAsync().ConfigureAwait(false);
		_fetcher.Dispose();
	}

	/// <summary>
	/// Tells the server what kind of in-place patches this runtime can apply, right after every (re)connect.
	/// </summary>
	private Task SendCapabilitiesAsync()
	{
		var (supported, capabilities) = PatchApplier.GetCapabilities();
		return _connection.SendAsync(new CapabilitiesMessage(ProtocolVersion.Current, supported, capabilities));
	}

	/// <summary>
	/// Dispatches one server message. The protocol is small: hello (catch up), generation (full swap), patch (in-place), errors,
	/// css, rebuilt/reload (the app itself changed), and compiling/nochanges for the overlay.
	/// </summary>
	private async Task HandleAsync(ServerMessage message)
	{
		switch (message)
		{
			case HelloMessage hello:
				await OnHelloAsync(hello).ConfigureAwait(true);
				break;

			case CompilingMessage:
				_runtime.SetStatus(new HotReloadStatus(HotReloadStatusKind.Compiling, "Hot reload: compiling...", []));
				break;

			case NoChangesMessage:
				_runtime.SetStatus(new HotReloadStatus(HotReloadStatusKind.Applied, "Hot reload: no changes to apply", []));
				break;

			// After a fresh start request, the reloaded page catches up with this generation.
			// A desktop page reload keeps the process (and the connection), so the server won't resend the generation: apply it first.
			case GenerationMessage generation when _freshStartRequested:
				if (await ApplyGenerationAsync(generation.Generation, generation.Assemblies).ConfigureAwait(true))
				{
					await ReloadPageAsync().ConfigureAwait(true);
				}

				break;

			case GenerationMessage generation:
				await ApplyGenerationAsync(generation.Generation, generation.Assemblies).ConfigureAwait(true);
				break;

			case PatchMessage patch:
				await OnPatchAsync(patch).ConfigureAwait(true);
				break;

			case ErrorsMessage errors:
				// Nothing fresh to start over with: the code doesn't compile.
				_freshStartRequested = false;
				var details = errors.Diagnostics.Select(d => d.ToString()).ToList();
				Console.WriteLine($"[hotreload] Compilation failed:\n{string.Join('\n', details)}");
				_runtime.SetStatus(
					new HotReloadStatus(HotReloadStatusKind.CompileErrors, $"Hot reload: {details.Count} compiler error(s)", details)
				);
				break;

			case CssMessage css:
				await _page.ReloadCssAsync(css.Path).ConfigureAwait(true);
				break;

			case RebuiltMessage:
				_runtime.SetStatus(
					new HotReloadStatus(
						HotReloadStatusKind.Failed,
						"The app was rebuilt: restart the dev server and reload the page (or run blazor-hmr with --devserver)",
						[]
					)
				);
				break;

			case ReloadMessage:
				await ReloadPageAsync().ConfigureAwait(true);
				break;

			default:
				Console.WriteLine($"[hotreload] Unknown message '{message.GetType().Name}'");
				break;
		}
	}

	/// <summary>
	/// Catch up with the server: its current generation, plus the in-place patches on top of it. A hello is also sent after a
	/// reconnect, so this handles both the first connection and a page that missed messages.
	/// </summary>
	private async Task OnHelloAsync(HelloMessage message)
	{
		if (message.ProtocolVersion != ProtocolVersion.Current)
		{
			Console.WriteLine(
				$"[hotreload] Protocol mismatch: the server speaks version {message.ProtocolVersion}, this app version {ProtocolVersion.Current}"
			);
		}

		// A new session means the server started over from a new build output (or restarted), so its generation numbers
		// start over too.
		var isNewSession = _sequence.IsNewSession(message.Session);
		_sequence.StartSession(message.Session);

		foreach (var path in message.Css)
		{
			await _page.ReloadCssAsync(path).ConfigureAwait(true);
		}

		// The server started over from the build output, while we run something else: get back in sync.
		if (isNewSession && IsModified)
		{
			await ReloadPageAsync().ConfigureAwait(true);
			return;
		}

		if (
			message.Generation != _sequence.Generation
			&& !await ApplyGenerationAsync(message.Generation, message.Assemblies).ConfigureAwait(true)
		)
		{
			return;
		}

		// The hello carries the patches as server messages (so they keep their "type" on the wire); only patches apply here.
		foreach (var patch in message.Patches.OfType<PatchMessage>())
		{
			if (patch.Sequence > _sequence.AppliedPatches && !await ApplyPatchAsync(patch).ConfigureAwait(true))
			{
				return;
			}
		}
	}

	private async Task OnPatchAsync(PatchMessage message)
	{
		if (_sequence.IsAlreadyApplied(message.Generation, message.Sequence))
		{
			return;
		}

		// Missed something (e.g. a generation that failed to load): let the server start a new generation.
		if (!_sequence.IsNextPatch(message.Generation, message.Sequence))
		{
			Console.WriteLine(
				$"[hotreload] Out of sync (at {_sequence.Generation}.{_sequence.AppliedPatches}, got {message.Generation}.{message.Sequence})"
			);
			await _connection.SendAsync(new PatchFailedMessage()).ConfigureAwait(true);
			return;
		}

		await ApplyPatchAsync(message).ConfigureAwait(true);
	}

	private async Task<bool> ApplyPatchAsync(PatchMessage message)
	{
		// E.g. a page that was loaded before hot reload enabled in-place patches: a reload fixes that (the reloaded page catches up).
		// Not a full swap, that would throw away the state of all the other pages.
		if (!PatchApplier.GetCapabilities().Supported)
		{
			if (!await _page.ReloadOnceAsync($"unsupported-{message.Session}").ConfigureAwait(true))
			{
				_runtime.SetStatus(
					new HotReloadStatus(HotReloadStatusKind.Failed, "Hot reload: this page can't apply in-place patches, reload it", [])
				);
			}

			return false;
		}

		try
		{
			await _runtime.InPlace.ApplyAsync(message.Changes).ConfigureAwait(true);
			_sequence.AppliedPatch(message.Sequence);
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[hotreload] Failed to apply patch {message.Generation}.{message.Sequence} in place: {ex}");
			_runtime.SetStatus(
				new HotReloadStatus(HotReloadStatusKind.Compiling, "Hot reload: could not update in place, restarting the app...", [])
			);
			await _connection.SendAsync(new PatchFailedMessage()).ConfigureAwait(true);
			return false;
		}
	}

	private async Task<bool> ApplyGenerationAsync(int generation, IReadOnlyList<string>? assemblyNames)
	{
		if (generation <= 0 || assemblyNames is null || assemblyNames.Count == 0)
		{
			return true;
		}

		// Old assemblies can't be unloaded, so every now and then, start over (the reloaded page will pick up the latest generation).
		if (_runtime.Current.Number >= HotReloadOptions.MaxGenerations)
		{
			await ReloadPageAsync().ConfigureAwait(true);
			return false;
		}

		try
		{
			// The app assembly, plus the libraries that changed since the last build.
			var assemblies = await _fetcher.FetchAsync(generation, assemblyNames, _connection.StoppingToken).ConfigureAwait(true);

			await _runtime.FullSwap.ApplyAsync(assemblies).ConfigureAwait(true);
			_sequence.AppliedGeneration(generation);
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[hotreload] Failed to apply generation {generation}: {ex}");
			_runtime.SetStatus(
				new HotReloadStatus(HotReloadStatusKind.Failed, "Hot reload failed, the previous version is still running", [ex.ToString()])
			);
			return false;
		}
	}

	private Task ReloadPageAsync()
	{
		_freshStartRequested = false;
		return _page.ReloadPageAsync();
	}
}
