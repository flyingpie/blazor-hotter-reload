namespace BlazorHMR.Server.Server;

/// <summary>
/// The stylesheets that changed since the build. The dev server keeps serving static assets with the ETag of the last build,
/// so a reloaded page would use its cached (old) version; new clients are told to fetch these again.
/// </summary>
internal sealed class CssChangeTracker(Lock gate)
{
	private readonly HashSet<string> _changed = new(StringComparer.Ordinal);

	public void Add(string path)
	{
		lock (gate)
		{
			_changed.Add(path);
		}
	}

	public IReadOnlyList<string> Snapshot()
	{
		lock (gate)
		{
			return [.. _changed];
		}
	}

	public void Reset()
	{
		lock (gate)
		{
			_changed.Clear();
		}
	}
}
