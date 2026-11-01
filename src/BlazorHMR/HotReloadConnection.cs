using System.Net.WebSockets;
using System.Text.Json;
using BlazorHMR.Protocol;

namespace BlazorHMR;

/// <summary>
/// Keeps a WebSocket connection to the hot reload server, reconnecting with backoff. On every (re)connect it sends the
/// client's capabilities and then forwards received messages to a handler. Knows nothing about generations or patches.
/// </summary>
internal sealed class HotReloadConnection : IAsyncDisposable
{
	private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

	private readonly Uri _serverUrl;
	private readonly Func<ServerMessage, Task> _onMessage;
	private readonly Func<Task> _onConnected;
	private readonly CancellationTokenSource _stop = new();

	private bool _reportedUnreachable;
	private Task? _loop;
	private ClientWebSocket? _socket;

	public HotReloadConnection(Uri serverUrl, Func<ServerMessage, Task> onMessage, Func<Task> onConnected)
	{
		_serverUrl = serverUrl;
		_onMessage = onMessage;
		_onConnected = onConnected;
	}

	/// <summary>
	/// Whether a connection is currently usable for sending.
	/// </summary>
	public bool IsOpen => _socket is { State: WebSocketState.Open };

	/// <summary>
	/// Cancelled when the connection is disposed, for callers that do their own I/O alongside it (e.g. downloads).
	/// </summary>
	public CancellationToken StoppingToken => _stop.Token;

	/// <summary>
	/// Starts the connection loop in the background. It reconnects forever (until disposed); the caller never awaits it.
	/// </summary>
	public void Start() => _loop = RunAsync(_stop.Token);

	/// <summary>
	/// Sends a message if the socket is open; otherwise drops it (there is nothing useful to queue for a dead socket).
	/// </summary>
	public async Task SendAsync(ClientMessage message)
	{
		if (_socket is { State: WebSocketState.Open } socket)
		{
			var payload = JsonSerializer.SerializeToUtf8Bytes<ClientMessage>(message, ProtocolJson.Options);
			await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, _stop.Token).ConfigureAwait(true);
		}
	}

	/// <summary>
	/// Cancels the loop and waits for it, so the socket isn't used after disposal.
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
	}

	/// <summary>
	/// Connects, reconnecting with backoff. On every (re)connect, the server sends a hello with its current state, so a client
	/// that missed messages can catch up.
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

				await _onConnected().ConfigureAwait(true);
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
			ServerMessage? payload;
			try
			{
				payload = await JsonSerializer.DeserializeAsync<ServerMessage>(message, ProtocolJson.Options, token).ConfigureAwait(true);
			}
			catch (JsonException ex)
			{
				// An unknown or malformed message shouldn't drop the connection; the next one may still be fine.
				Console.WriteLine($"[hotreload] Ignoring an invalid server message: {ex.Message}");
				continue;
			}

			if (payload is not null)
			{
				await _onMessage(payload).ConfigureAwait(true);
			}
		}
	}
}
