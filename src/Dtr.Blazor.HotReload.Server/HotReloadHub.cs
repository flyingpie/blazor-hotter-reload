using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Dtr.Blazor.HotReload.Server.Compiling;

namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// The compiled generations, and the browsers that are connected to us.
/// </summary>
internal sealed class HotReloadHub
{
	private const int GenerationsToKeep = 5;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly Lock _lock = new();
	private readonly Dictionary<int, Dictionary<string, (byte[] Assembly, byte[] Symbols)>> _generations = [];
	private readonly List<Client> _clients = [];
	private readonly HashSet<string> _changedCss = new(StringComparer.Ordinal);

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
			generation = ++Generation;
			_generations[generation] = assemblies.ToDictionary(
				a => a.Key,
				a => (a.Value.Assembly!, a.Value.Symbols!),
				StringComparer.OrdinalIgnoreCase
			);
			_generations.Remove(generation - GenerationsToKeep);
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
	/// Back to the assembly of the build output.
	/// </summary>
	public void Reset()
	{
		lock (_lock)
		{
			Session = NewSession();
			Generation = 0;
			_generations.Clear();
			_changedCss.Clear();
		}
	}

	/// <summary>
	/// The DevServer keeps serving static assets with the ETag of the last build, so after a page reload, the browser would use its
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

	public (byte[] Assembly, byte[] Symbols)? GetAssembly(int generation, string assemblyName)
	{
		lock (_lock)
		{
			return _generations.TryGetValue(generation, out var assemblies) && assemblies.TryGetValue(assemblyName, out var value)
				? value
				: null;
		}
	}

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

	public async Task HandleAsync(WebSocket socket, CancellationToken token)
	{
		var client = new Client(socket);
		lock (_lock)
		{
			// Hello goes first, and nothing can be broadcast in between.
			var hello = new
			{
				type = "hello",
				session = Session,
				generation = Generation,
				assemblies = _generations.TryGetValue(Generation, out var assemblies) ? assemblies.Keys.ToList() : [],
				css = _changedCss.ToList(),
			};
			client.Send(JsonSerializer.SerializeToUtf8Bytes(hello, JsonOptions));
			_clients.Add(client);
		}

		Log.Info($"Browser connected ({ClientCount} total)");
		try
		{
			// The browser doesn't send anything (yet), this just waits for it to go away.
			var buffer = new byte[4096];
			while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
			{
				var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
					break;
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
			Log.Info($"Browser disconnected ({ClientCount} total)");
		}
	}

	private static string NewSession() => Guid.NewGuid().ToString("N");

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
