using System.Net.WebSockets;
using System.Text.Json;
using BlazorHMR.Protocol;
using BlazorHMR.Server.Compilation;
using BlazorHMR.Server.InPlace;

namespace BlazorHMR.Server.Server;

/// <summary>
/// The server-side state that the compile worker and the WebSocket endpoint share: the generations, the patches on top of
/// them, the connected clients, and the changed stylesheets. This class only coordinates them; each concern lives in its own
/// type.
/// </summary>
internal sealed class HotReloadHub
{
	// One gate for all state: a client's hello has to see the generation, patches, and CSS from one consistent moment, and a
	// generation must not be published in between adding the client and sending its hello. The stores lock the same gate.
	private readonly Lock _gate = new();
	private readonly GenerationStore _generations;
	private readonly PatchHistory _patches;
	private readonly CssChangeTracker _css;
	private readonly ClientHub _clients;

	public HotReloadHub()
	{
		_generations = new GenerationStore(_gate);
		_patches = new PatchHistory(_gate);
		_css = new CssChangeTracker(_gate);
		_clients = new ClientHub(_gate);
	}

	/// <summary>
	/// A message from a client.
	/// </summary>
	public event Action<ClientMessage>? ClientMessage;

	/// <summary>
	/// Changes whenever we start over from a new build output (generation 0), so clients can tell that numbers have been reset.
	/// </summary>
	public string Session { get; private set; } = NewSession();

	public int Generation => _generations.Current;

	public int ClientCount => _clients.Count;

	/// <summary>
	/// Publishes a new generation: the app assembly, plus all libraries that changed since the last build.
	/// </summary>
	public int Publish(IReadOnlyDictionary<string, CompileResult> assemblies)
	{
		ArgumentNullException.ThrowIfNull(assemblies);

		int generation;
		lock (_gate)
		{
			generation = _generations.Add(assemblies);

			// A new generation invalidates the patches that were on top of the previous one.
			_patches.Clear();
		}

		Broadcast(new GenerationMessage(generation, assemblies.Keys.ToList()));
		return generation;
	}

	/// <summary>
	/// Back to the assembly of the build output: forget all generations and patches, and start a new session, so connected
	/// clients know that generation numbers restarted and that they need to resync (or reload).
	/// </summary>
	public void Reset()
	{
		lock (_gate)
		{
			Session = NewSession();
			_generations.Reset();
			_patches.Clear();
			_css.Reset();
		}
	}

	/// <summary>
	/// Publishes in-place patches for the current generation. Returns the sequence number of the patch within the generation.
	/// </summary>
	public int PublishPatch(IReadOnlyList<PatchUpdate> changes)
	{
		ArgumentNullException.ThrowIfNull(changes);

		PatchMessage message;
		int sequence;
		lock (_gate)
		{
			sequence = _patches.Count + 1;
			message = new PatchMessage(
				Session,
				_generations.Current,
				sequence,
				[.. changes.Select(c => c.ToPatchChange())]
			);
			_patches.Add(message);
		}

		Broadcast(message);
		return sequence;
	}

	/// <summary>
	/// Remembers a changed stylesheet, and tells connected clients to reload it.
	/// </summary>
	public void PublishCss(string path)
	{
		_css.Add(path);
		Broadcast(new CssMessage(path));
	}

	/// <summary>
	/// The assembly + PDB of a generation, for the client's download endpoint. Null when the generation has been evicted, or
	/// the assembly wasn't part of it.
	/// </summary>
	public (byte[] Assembly, byte[] Symbols)? GetAssembly(int generation, string assemblyName) =>
		_generations.Get(generation, assemblyName);

	/// <summary>
	/// Sends a message to every connected client. Serialization happens once, outside the lock; each client's send loop does
	/// the actual (single-at-a-time) WebSocket send.
	/// </summary>
	public void Broadcast(ServerMessage message)
	{
		// Serialize as the base type, so the polymorphic "type" discriminator is included.
		var payload = JsonSerializer.SerializeToUtf8Bytes<ServerMessage>(message, ProtocolJson.Options);
		_clients.Broadcast(payload);
	}

	/// <summary>
	/// Serves one WebSocket: send the current state, then read client messages until the socket closes.
	/// </summary>
	public async Task HandleAsync(WebSocket socket, CancellationToken token)
	{
		var client = new ClientHub.Client(socket);
		lock (_gate)
		{
			// Hello goes first, and nothing can be broadcast in between: a client either sees the state before it was added
			// (and catches up) or receives every subsequent message (and stays current).
			var hello = new HelloMessage(
				ProtocolVersion.Current,
				Session,
				_generations.Current,
				_generations.AssemblyNames(_generations.Current),
				_patches.Snapshot(),
				_css.Snapshot()
			);
			client.Send(JsonSerializer.SerializeToUtf8Bytes<ServerMessage>(hello, ProtocolJson.Options));
			_clients.Add(client);
		}

		Log.Info($"Client connected ({ClientCount} total)");
		try
		{
			// Client messages are small JSON objects; a WebSocket message may still be split into multiple frames, so collect
			// them until EndOfMessage.
			var buffer = new byte[4096];
			using var message = new MemoryStream();
			while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
			{
				var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
					break;
				}

				await message.WriteAsync(buffer.AsMemory(0, result.Count), token).ConfigureAwait(false);
				if (result.EndOfMessage)
				{
					OnClientMessage(message.ToArray());
					message.SetLength(0);
				}
			}
		}
		catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
		{
			// Gone
		}
		finally
		{
			_clients.Remove(client);
			client.Complete();
			await client.SendLoop.ConfigureAwait(false);
			Log.Info($"Client disconnected ({ClientCount} total)");
		}
	}

	private static string NewSession() => Guid.NewGuid().ToString("N");

	/// <summary>
	/// Deserializes a client message and forwards it. Unknown or malformed messages are logged and ignored; they must not
	/// take the connection down.
	/// </summary>
	private void OnClientMessage(byte[] payload)
	{
		try
		{
			if (JsonSerializer.Deserialize<ClientMessage>(payload, ProtocolJson.Options) is { } message)
			{
				ClientMessage?.Invoke(message);
			}
		}
		catch (JsonException ex)
		{
			Log.Warning($"Invalid message from client: {ex.Message}");
		}
	}
}
