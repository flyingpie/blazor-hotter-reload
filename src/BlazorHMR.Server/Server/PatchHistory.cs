using BlazorHMR.Protocol;

namespace BlazorHMR.Server.Server;

/// <summary>
/// The in-place patches on top of the current generation, in order. A client that reconnects gets them in the hello, so it
/// can catch up. A new generation clears the history.
/// </summary>
internal sealed class PatchHistory(Lock gate)
{
	private readonly List<PatchMessage> _patches = [];

	public int Count
	{
		get
		{
			lock (gate)
			{
				return _patches.Count;
			}
		}
	}

	public void Add(PatchMessage message)
	{
		lock (gate)
		{
			_patches.Add(message);
		}
	}

	/// <summary>
	/// A copy of the patches. The hello declares its patch list as <see cref="ServerMessage"/>, so each patch keeps its
	/// <c>type</c> on the wire (the covariance conversion is implicit).
	/// </summary>
	public IReadOnlyList<PatchMessage> Snapshot()
	{
		lock (gate)
		{
			return [.. _patches];
		}
	}

	public void Clear()
	{
		lock (gate)
		{
			_patches.Clear();
		}
	}
}
