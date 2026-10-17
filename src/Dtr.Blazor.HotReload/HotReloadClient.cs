using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Dtr.Blazor.HotReload;

/// <summary>
/// Talks to the hot reload server over a WebSocket, and applies what it sends: complete new generations of the app.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Hot reload must never take down the app")]
internal sealed class HotReloadClient : IAsyncDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

	private readonly HotReloadRuntime _runtime;
	private readonly IServiceProvider _hostServices;
	private readonly Uri _serverUrl;
	private readonly HttpClient _http;
	private readonly CancellationTokenSource _stop = new();

	private string? _session;
	private int _serverGeneration;
	private bool _reportedUnreachable;
	private Task? _loop;
	private IJSObjectReference? _module;

	public HotReloadClient(HotReloadRuntime runtime, IServiceProvider hostServices)
	{
		_runtime = runtime;
		_hostServices = hostServices;
		_serverUrl = runtime.Options.ServerUrl;
		_http = new HttpClient { BaseAddress = _serverUrl };
	}

	/// <summary>
	/// Whether this page runs anything else than the build output.
	/// </summary>
	private bool IsModified => _serverGeneration > 0 || _runtime.Current.Number > 0;

	public void Start() => _loop = RunAsync(_stop.Token);

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
						$"[hotreload] Server not reachable at {socketUrl} (start the hot reload server, dtr-blazor-hotreload, to enable hot reload): {ex.Message}"
					);
				}
			}

			await Task.Delay(delay, token).ConfigureAwait(true);
			delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
		}
	}

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

			case "update":
				await ApplyGenerationAsync(message.Generation, message.Assemblies).ConfigureAwait(true);
				break;

			case "errors":
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
					new HotReloadStatus(HotReloadStatusKind.Failed, "The app was rebuilt: restart the dev server and reload the page", [])
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
	/// Catch up with the server: its current generation.
	/// </summary>
	private async Task OnHelloAsync(ServerMessage message)
	{
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

		if (message.Generation != _serverGeneration)
		{
			await ApplyGenerationAsync(message.Generation, message.Assemblies).ConfigureAwait(true);
		}
	}

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
		if (_runtime.Current.Number >= _runtime.Options.MaxGenerations)
		{
			await ReloadPageAsync().ConfigureAwait(true);
			return false;
		}

		try
		{
			var assemblies = await Task.WhenAll(assemblyNames.Select(name => FetchAssemblyAsync(generation, name))).ConfigureAwait(true);

			await _runtime.ApplyAsync(assemblies).ConfigureAwait(true);
			_serverGeneration = generation;
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

	private async Task<IJSObjectReference> GetModuleAsync()
	{
		var js = _hostServices.GetRequiredService<IJSRuntime>();
		return _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/Dtr.Blazor.HotReload/hotreload.js")
			.ConfigureAwait(true);
	}

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

	private Task ReloadPageAsync()
	{
		Console.WriteLine("[hotreload] Reloading page");
		_hostServices.GetRequiredService<NavigationManager>().Refresh(forceReload: true);
		return Task.CompletedTask;
	}

	private sealed record ServerMessage(
		string Type,
		string? Session,
		int Generation,
		string? Path,
		IReadOnlyList<string>? Css,
		IReadOnlyList<string>? Assemblies,
		IReadOnlyList<HotReloadDiagnostic>? Diagnostics
	);
}
