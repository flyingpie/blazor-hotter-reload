using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BlazorHMR.Server.Compiling;
using BlazorHMR.Server.Deltas;

namespace BlazorHMR.Server;

/// <summary>
/// The compiled generations, and the clients that are connected to us.
/// </summary>
internal sealed class HotReloadHub
{
	// A reconnecting client may be a few generations behind. Keep enough to serve it, but don't keep every assembly forever.
	private const int GenerationsToKeep = 5;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly Lock _lock = new();
	private readonly Dictionary<int, Dictionary<string, (byte[] Assembly, byte[] Symbols)>> _generations = [];
	private readonly List<Client> _clients = [];
	private readonly HashSet<string> _changedCss = new(StringComparer.Ordinal);

	/// <summary>
	/// The in-place updates (Edit-and-Continue deltas) on top of the current generation, in order.
	/// </summary>
	private readonly List<object> _deltas = [];

	/// <summary>
	/// A message from a client: its type, and the whole message.
	/// </summary>
	public event Action<string, JsonElement>? ClientMessage;

	/// <summary>
	/// Changes whenever we start over from a new build output (generation 0), so clients can tell that numbers have been reset.
	/// </summary>
	public string Session { get; private set; } = NewSession();

	public int Generation { get; private set; }

	public int ClientCount
	{
		get
		{
			lock (_lock)
			{
				return _clients.Count;
			}
		}
	}

	/// <summary>
	/// Publishes a new generation: the app assembly, plus all libraries that changed since the last build.
	/// </summary>
	public int Publish(IReadOnlyDictionary<string, CompileResult> assemblies)
	{
		ArgumentNullException.ThrowIfNull(assemblies);

		int generation;
		lock (_lock)
		{
			// A new generation invalidates the deltas that were on top of the previous one.
			generation = ++Generation;
			_generations[generation] = assemblies.ToDictionary(
				a => a.Key,
				a => (a.Value.Assembly!, a.Value.Symbols!),
				StringComparer.OrdinalIgnoreCase
			);
			_generations.Remove(generation - GenerationsToKeep);
			_deltas.Clear();
		}

		Broadcast(
			new
			{
				type = "update",
				generation,
				assemblies = assemblies.Keys.ToList(),
			}
		);
		return generation;
	}

	/// <summary>
	/// Back to the assembly of the build output: forget all generations and deltas, and start a new session, so connected
	/// clients know that generation numbers restarted and that they need to resync (or reload).
	/// </summary>
	public void Reset()
	{
		lock (_lock)
		{
			Session = NewSession();
			Generation = 0;
			_generations.Clear();
			_changedCss.Clear();
			_deltas.Clear();
		}
	}

	/// <summary>
	/// Publishes in-place updates for the current generation. Returns the sequence number of the delta within the generation.
	/// </summary>
	public int PublishDelta(IReadOnlyList<DeltaUpdate> updates)
	{
		ArgumentNullException.ThrowIfNull(updates);

		object message;
		int sequence;
		lock (_lock)
		{
			sequence = _deltas.Count + 1;
			message = new
			{
				type = "delta",
				session = Session,
				generation = Generation,
				sequence,
				updates = updates
					.Select(u => new
					{
						moduleId = u.ModuleId,
						metadata = u.Metadata,
						il = u.IL,
						pdb = u.Pdb,
						updatedTypes = u.UpdatedTypes,
					})
					.ToList(),
			};
			_deltas.Add(message);
		}

		Broadcast(message);
		return sequence;
	}

	/// <summary>
	/// The DevServer keeps serving static assets with the ETag of the last build, so after a page reload, the client would use its
	/// cached (old) version. So we remember which stylesheets changed, and tell new clients to fetch them again.
	/// </summary>
	public void PublishCss(string path)
	{
		lock (_lock)
		{
			_changedCss.Add(path);
		}

		Broadcast(new { type = "css", path });
	}

	/// <summary>
	/// The assembly + PDB of a generation, for the client's download endpoint. Null when the generation has been evicted, or
	/// the assembly wasn't part of it.
	/// </summary>
	public (byte[] Assembly, byte[] Symbols)? GetAssembly(int generation, string assemblyName)
	{
		lock (_lock)
		{
			return _generations.TryGetValue(generation, out var assemblies) && assemblies.TryGetValue(assemblyName, out var value)
				? value
				: null;
		}
	}

	/// <summary>
	/// Sends a message to every connected client. Serialization happens once, outside the lock; each client's send loop does
	/// the actual (single-at-a-time) WebSocket send.
	/// </summary>
	public void Broadcast(object message)
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
		List<Client> clients;
		lock (_lock)
		{
			clients = [.. _clients];
		}

		foreach (var client in clients)
		{
			client.Send(payload);
		}
	}

	/// <summary>
	/// Serves one WebSocket: send the current state, then read client messages until the socket closes.
	/// </summary>
	public async Task HandleAsync(WebSocket socket, CancellationToken token)
	{
		var client = new Client(socket);
		lock (_lock)
		{
			// Hello goes first, and nothing can be broadcast in between: a client either sees the state before it was added
			// (and catches up) or receives every subsequent message (and stays current).
			var hello = new
			{
				type = "hello",
				session = Session,
				generation = Generation,
				assemblies = _generations.TryGetValue(Generation, out var assemblies) ? assemblies.Keys.ToList() : [],
				deltas = _deltas.ToList(),
				css = _changedCss.ToList(),
			};
			client.Send(JsonSerializer.SerializeToUtf8Bytes(hello, JsonOptions));
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
			lock (_lock)
			{
				_clients.Remove(client);
			}

			client.Complete();
			await client.SendLoop.ConfigureAwait(false);
			Log.Info($"Client disconnected ({ClientCount} total)");
		}
	}

	private static string NewSession() => Guid.NewGuid().ToString("N");

	/// <summary>
	/// Parses the message type and forwards the whole message. The <see cref="JsonElement"/> is cloned because the document is
	/// disposed when this method returns.
	/// </summary>
	private void OnClientMessage(byte[] payload)
	{
		try
		{
			using var json = JsonDocument.Parse(payload);
			if (json.RootElement.TryGetProperty("type", out var type) && type.GetString() is { } name)
			{
				ClientMessage?.Invoke(name, json.RootElement.Clone());
			}
		}
		catch (JsonException ex)
		{
			Log.Warning($"Invalid message from client: {ex.Message}");
		}
	}

	/// <summary>
	/// A WebSocket only allows one send at a time, so messages are queued, and sent by a single loop.
	/// </summary>
	private sealed class Client
	{
		private readonly WebSocket _socket;
		private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

		public Client(WebSocket socket)
		{
			_socket = socket;
			SendLoop = SendLoopAsync();
		}

		public Task SendLoop { get; }

		public void Send(byte[] payload) => _outbox.Writer.TryWrite(payload);

		public void Complete() => _outbox.Writer.TryComplete();

		private async Task SendLoopAsync()
		{
			await foreach (var payload in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
			{
				try
				{
					if (_socket.State == WebSocketState.Open)
					{
						await _socket
							.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
							.ConfigureAwait(false);
					}
				}
				catch (WebSocketException)
				{
					// Gone, will be cleaned up by the receive loop
				}
			}
		}
	}
}
