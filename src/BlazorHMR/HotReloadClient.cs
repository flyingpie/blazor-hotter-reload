using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using BlazorHMR.Compilation;
using BlazorHMR.Deltas;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorHMR;

/// <summary>
/// Talks to the hot reload server over a WebSocket, and applies what it sends:
/// in-place updates (deltas, which keep all state), or complete new generations of the app.
/// </summary>
public sealed class HotReloadClient : IAsyncDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

	private readonly HotReloadRuntime _runtime;
	private IServiceProvider _hostServices;
	private readonly Uri _serverUrl;
	private readonly HttpClient _http;
	private readonly CancellationTokenSource _stop = new();

	private string? _session;
	private int _serverGeneration;
	private int _appliedDeltas;
	private bool _reportedUnreachable;
	private bool _freshStartRequested;
	private Task? _loop;
	private ClientWebSocket? _socket;
	private IJSObjectReference? _module;

	public HotReloadClient(HotReloadRuntime runtime, IServiceProvider hostServices)
	{
		_runtime = runtime;
		_hostServices = hostServices;
		_serverUrl = HotReloadOptions.ServerUrl;
		_http = new HttpClient { BaseAddress = _serverUrl };
	}

	/// <summary>
	/// Whether this page runs anything else than the build output.
	/// </summary>
	private bool IsModified => _serverGeneration > 0 || _appliedDeltas > 0 || _runtime.Current.Number > 0;

	/// <summary>
	/// Starts the connection loop in the background. It reconnects forever (until disposed); the runtime never awaits it.
	/// </summary>
	public void Start() => _loop = RunAsync(_stop.Token);

	/// <summary>
	/// A desktop page reload keeps this process (and this connection) alive, but the page's services and JS runtime are new.
	/// </summary>
	public void Reattach(IServiceProvider hostServices)
	{
		_hostServices = hostServices;

		// The JS module was imported into the old page's JS runtime, so it has to be imported again on the next call.
		_module = null;
	}

	/// <summary>
	/// Asks the server for a full swap (a new generation, compiled from scratch).
	/// </summary>
	public Task RequestRestartAsync() => SendAsync(new { type = "restart" });

	/// <summary>
	/// For when the page got into a weird state: asks the server for a full swap, and reloads the page once it's there (instead of
	/// swapping it in), so everything starts over (DOM, JS, state, no old generations), running the latest code.
	/// Without a connection to the server, just reloads the page.
	/// </summary>
	public async Task RequestFreshStartAsync()
	{
		if (_socket is not { State: WebSocketState.Open })
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
	/// Cancels the loop and waits for it, so the socket and HTTP client aren't used after disposal.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		await _stop.CancelAsync().ConfigureAwait(false);
		if (_loop is not null)
		{
			try
			{
#pragma warning disable VSTHRD003 // The loop was started by us (in Start)
				await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch (OperationCanceledException)
			{
				// Expected
			}
		}

		_stop.Dispose();
		_http.Dispose();
	}

	/// <summary>
	/// Keeps a WebSocket connection to the server, reconnecting with backoff. On every (re)connect, the server sends a hello with
	/// its current state, so a page that missed messages can catch up.
	/// </summary>
	private async Task RunAsync(CancellationToken token)
	{
		var socketUrl = new UriBuilder(new Uri(_serverUrl, "hotreload"))
		{
			Scheme = _serverUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
		}.Uri;
		var delay = TimeSpan.FromMilliseconds(250);

		while (!token.IsCancellationRequested)
		{
			using var socket = new ClientWebSocket();
			try
			{
				await socket.ConnectAsync(socketUrl, token).ConfigureAwait(true);
				Console.WriteLine($"[hotreload] Connected to {socketUrl}");
				_reportedUnreachable = false;
				delay = TimeSpan.FromMilliseconds(250);
				_socket = socket;

				// Tell the server what kind of in-place updates this runtime can apply.
				var (supported, capabilities) = DeltaApplier.GetCapabilities();
				await SendAsync(
						new
						{
							type = "capabilities",
							supported,
							capabilities,
						}
					)
					.ConfigureAwait(true);

				await ReceiveLoopAsync(socket, token).ConfigureAwait(true);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				if (!_reportedUnreachable)
				{
					_reportedUnreachable = true;
					Console.WriteLine(
						$"[hotreload] Server not reachable at {socketUrl} (start the hot reload server, blazor-hmr, to enable hot reload): {ex.Message}"
					);
				}
			}
			finally
			{
				_socket = null;
			}

			// Back off after a failed connection or a disconnect, but don't wait longer than MaxReconnectDelay.
			await Task.Delay(delay, token).ConfigureAwait(true);
			delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
		}
	}

	/// <summary>
	/// Sends a message if the socket is open; otherwise drops it (there is nothing useful to queue for a dead socket).
	/// </summary>
	private async Task SendAsync(object message)
	{
		if (_socket is { State: WebSocketState.Open } socket)
		{
			var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
			await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, _stop.Token).ConfigureAwait(true);
		}
	}

	/// <summary>
	/// Receives messages until the socket closes. A WebSocket message can arrive in multiple frames, so collect them until
	/// <see cref="WebSocketReceiveResult.EndOfMessage"/> before deserializing.
	/// </summary>
	private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
	{
		var buffer = new byte[64 * 1024];
		using var message = new MemoryStream();

		while (socket.State == WebSocketState.Open)
		{
			message.SetLength(0);
			WebSocketReceiveResult result;
			do
			{
				result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(true);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					return;
				}

				await message.WriteAsync(buffer.AsMemory(0, result.Count), token).ConfigureAwait(true);
			} while (!result.EndOfMessage);

			message.Position = 0;
			var payload = await JsonSerializer.DeserializeAsync<ServerMessage>(message, JsonOptions, token).ConfigureAwait(true);
			if (payload is not null)
			{
				await HandleAsync(payload).ConfigureAwait(true);
			}
		}
	}

	/// <summary>
	/// Dispatches one server message. The protocol is small: hello (catch up), update (full swap), delta (in-place), errors, css,
	/// rebuilt/reload (the app itself changed), and compiling/nochanges for the overlay.
	/// </summary>
	private async Task HandleAsync(ServerMessage message)
	{
		switch (message.Type)
		{
			case "hello":
				await OnHelloAsync(message).ConfigureAwait(true);
				break;

			case "compiling":
				_runtime.SetStatus(new HotReloadStatus(HotReloadStatusKind.Compiling, "Hot reload: compiling...", []));
				break;

			case "nochanges":
				_runtime.SetStatus(new HotReloadStatus(HotReloadStatusKind.Applied, "Hot reload: no changes to apply", []));
				break;

			// After a fresh start request, the reloaded page catches up with this generation.
			// A desktop page reload keeps the process (and the connection), so the server won't resend the generation: apply it first.
			case "update" when _freshStartRequested && _runtime.ManagesHostScope:
				if (await ApplyGenerationAsync(message.Generation, message.Assemblies).ConfigureAwait(true))
				{
					await ReloadPageAsync().ConfigureAwait(true);
				}

				break;

			// After a fresh start request, the reloaded page catches up with this generation.
			case "update" when _freshStartRequested:
				await ReloadPageAsync().ConfigureAwait(true);
				break;

			case "update":
				await ApplyGenerationAsync(message.Generation, message.Assemblies).ConfigureAwait(true);
				break;

			case "delta":
				await OnDeltaAsync(message).ConfigureAwait(true);
				break;

			case "errors":
				// Nothing fresh to start over with: the code doesn't compile.
				_freshStartRequested = false;
				var details = (message.Diagnostics ?? []).Select(d => d.ToString()).ToList();
				Console.WriteLine($"[hotreload] Compilation failed:\n{string.Join('\n', details)}");
				_runtime.SetStatus(
					new HotReloadStatus(HotReloadStatusKind.CompileErrors, $"Hot reload: {details.Count} compiler error(s)", details)
				);
				break;

			case "css":
				await ReloadCssAsync(message.Path ?? string.Empty).ConfigureAwait(true);
				break;

			case "rebuilt":
				_runtime.SetStatus(
					new HotReloadStatus(
						HotReloadStatusKind.Failed,
						"The app was rebuilt: restart the dev server and reload the page (or run blazor-hmr with --devserver)",
						[]
					)
				);
				break;

			case "reload":
				await ReloadPageAsync().ConfigureAwait(true);
				break;

			default:
				Console.WriteLine($"[hotreload] Unknown message '{message.Type}'");
				break;
		}
	}

	/// <summary>
	/// Catch up with the server: its current generation, plus the in-place updates on top of it. A hello is also sent after a
	/// reconnect, so this handles both the first connection and a page that missed messages.
	/// </summary>
	private async Task OnHelloAsync(ServerMessage message)
	{
		// A new session means the server started over from a new build output (or restarted), so its generation numbers
		// start over too.
		var isNewSession = _session is not null && _session != message.Session;
		_session = message.Session;

		foreach (var path in message.Css ?? [])
		{
			await ReloadCssAsync(path).ConfigureAwait(true);
		}

		// The server started over from the build output, while we run something else: get back in sync.
		if (isNewSession && IsModified)
		{
			await ReloadPageAsync().ConfigureAwait(true);
			return;
		}

		if (
			message.Generation != _serverGeneration
			&& !await ApplyGenerationAsync(message.Generation, message.Assemblies).ConfigureAwait(true)
		)
		{
			return;
		}

		foreach (var delta in message.Deltas ?? [])
		{
			if (delta.Sequence > _appliedDeltas && !await ApplyDeltaAsync(delta).ConfigureAwait(true))
			{
				return;
			}
		}
	}

	private async Task OnDeltaAsync(ServerMessage message)
	{
		if (message.Sequence <= _appliedDeltas && message.Generation == _serverGeneration)
		{
			return;
		}

		// Missed something (e.g. a generation that failed to load): let the server start a new generation.
		if (message.Generation != _serverGeneration || message.Sequence != _appliedDeltas + 1)
		{
			Console.WriteLine(
				$"[hotreload] Out of sync (at {_serverGeneration}.{_appliedDeltas}, got {message.Generation}.{message.Sequence})"
			);
			await SendAsync(new { type = "delta-failed" }).ConfigureAwait(true);
			return;
		}

		await ApplyDeltaAsync(message).ConfigureAwait(true);
	}

	private async Task<bool> ApplyDeltaAsync(ServerMessage message)
	{
		// E.g. a page that was loaded before hot reload enabled in-place updates: a reload fixes that (the reloaded page catches up).
		// Not a full swap, that would throw away the state of all the other pages.
		if (!DeltaApplier.GetCapabilities().Supported)
		{
			if (!await ReloadOnceAsync($"unsupported-{message.Session}").ConfigureAwait(true))
			{
				_runtime.SetStatus(
					new HotReloadStatus(HotReloadStatusKind.Failed, "Hot reload: this page can't apply in-place updates, reload it", [])
				);
			}

			return false;
		}

		try
		{
			await _runtime.ApplyDeltaAsync(message.Updates ?? []).ConfigureAwait(true);
			_appliedDeltas = message.Sequence;
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[hotreload] Failed to apply update {message.Generation}.{message.Sequence} in place: {ex}");
			_runtime.SetStatus(
				new HotReloadStatus(HotReloadStatusKind.Compiling, "Hot reload: could not update in place, restarting the app...", [])
			);
			await SendAsync(new { type = "delta-failed" }).ConfigureAwait(true);
			return false;
		}
	}

	/// <summary>
	/// Downloads one assembly and its portable PDB. The server keeps a few recent generations, so a client that reconnects can
	/// still fetch the one it is missing.
	/// </summary>
	private async Task<GenerationAssembly> FetchAssemblyAsync(int generation, string name)
	{
		var assembly = await _http
			.GetByteArrayAsync(new Uri($"hotreload/{generation}/{name}.dll", UriKind.Relative), _stop.Token)
			.ConfigureAwait(true);
		var symbols = await _http
			.GetByteArrayAsync(new Uri($"hotreload/{generation}/{name}.pdb", UriKind.Relative), _stop.Token)
			.ConfigureAwait(true);

		return new GenerationAssembly(name, assembly, symbols);
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
			var assemblies = await Task.WhenAll(assemblyNames.Select(name => FetchAssemblyAsync(generation, name))).ConfigureAwait(true);

			await _runtime.ApplyAsync(assemblies).ConfigureAwait(true);
			_serverGeneration = generation;
			_appliedDeltas = 0;
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

	/// <summary>
	/// Imports the package's JS module once per page (the desktop resets it on <see cref="Reattach"/>).
	/// </summary>
	private async Task<IJSObjectReference> GetModuleAsync()
	{
		var js = _hostServices.GetRequiredService<IJSRuntime>();
		return _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/BlazorHMR/hotreload.js").ConfigureAwait(true);
	}

	/// <summary>
	/// Reloads the page at most once per <paramref name="key"/> in this tab, so a page that can't apply updates doesn't reload
	/// in a loop. Returns false when it was already reloaded for this key.
	/// </summary>
	private async Task<bool> ReloadOnceAsync(string key)
	{
		var module = await GetModuleAsync().ConfigureAwait(true);
		return await module.InvokeAsync<bool>("reloadOnce", key).ConfigureAwait(true);
	}

	/// <summary>
	/// CSS isn't compiled into an assembly, so reload the matching stylesheet(s) by cache-busting their URLs.
	/// </summary>
	private async Task ReloadCssAsync(string path)
	{
		try
		{
			var module = await GetModuleAsync().ConfigureAwait(true);
			await module.InvokeVoidAsync("reloadCss", path).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[hotreload] CSS reload failed: {ex.Message}");
		}
	}

	/// <summary>
	/// A forced navigation reload. In the browser this also discards the old generations; on the desktop the process stays alive,
	/// but the page (and its service scope) starts over.
	/// </summary>
	private Task ReloadPageAsync()
	{
		Console.WriteLine("[hotreload] Reloading page");
		_freshStartRequested = false;
		_module = null;
		_hostServices.GetRequiredService<NavigationManager>().Refresh(forceReload: true);
		return Task.CompletedTask;
	}

	private sealed record ServerMessage(
		string Type,
		string? Session,
		int Generation,
		int Sequence,
		string? Path,
		IReadOnlyList<string>? Css,
		IReadOnlyList<string>? Assemblies,
		IReadOnlyList<ServerMessage>? Deltas,
		IReadOnlyList<DeltaPayload>? Updates,
		IReadOnlyList<CompileDiagnostic>? Diagnostics
	);
}
