using System.Net.WebSockets;
using System.Threading.Channels;

namespace BlazorHMR.Server.Server;

/// <summary>
/// The connected WebSocket clients, and sending to all of them.
/// </summary>
internal sealed class ClientHub(Lock gate)
{
	private readonly List<Client> _clients = [];

	public int Count
	{
		get
		{
			lock (gate)
			{
				return _clients.Count;
			}
		}
	}

	public void Add(Client client)
	{
		lock (gate)
		{
			_clients.Add(client);
		}
	}

	public void Remove(Client client)
	{
		lock (gate)
		{
			_clients.Remove(client);
		}
	}

	/// <summary>
	/// Sends a serialized message to every connected client. The caller serializes once; each client's send loop does the
	/// actual (single-at-a-time) WebSocket send.
	/// </summary>
	public void Broadcast(byte[] payload)
	{
		List<Client> clients;
		lock (gate)
		{
			clients = [.. _clients];
		}

		foreach (var client in clients)
		{
			client.Send(payload);
		}
	}

	/// <summary>
	/// A WebSocket only allows one send at a time, so messages are queued, and sent by a single loop.
	/// </summary>
	public sealed class Client
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
