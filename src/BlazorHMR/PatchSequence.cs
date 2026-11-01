namespace BlazorHMR;

/// <summary>
/// Where this client is in the server's stream of changes: the session, the generation, and the patches applied on top of it.
/// Kept separate from the connection, so the "am I in sync?" decision is in one place.
/// </summary>
internal sealed class PatchSequence
{
	/// <summary>
	/// The server's session id. A new session means the server started over from a new build output (or restarted), so its
	/// generation numbers start over too.
	/// </summary>
	public string? Session { get; private set; }

	/// <summary>
	/// The generation that the app is running.
	/// </summary>
	public int Generation { get; private set; }

	/// <summary>
	/// The last in-place patch that was applied on top of <see cref="Generation"/>.
	/// </summary>
	public int AppliedPatches { get; private set; }

	/// <summary>
	/// Whether anything from the server has been applied (so a new session means the running code is out of date).
	/// </summary>
	public bool IsModified => Generation > 0 || AppliedPatches > 0;

	public bool IsNewSession(string session) => Session is not null && Session != session;

	public void StartSession(string session) => Session = session;

	/// <summary>
	/// Whether this patch is the next one in the current generation.
	/// </summary>
	public bool IsNextPatch(int generation, int sequence) => generation == Generation && sequence == AppliedPatches + 1;

	/// <summary>
	/// Whether this patch was already applied (the server may resend it in a hello after a reconnect).
	/// </summary>
	public bool IsAlreadyApplied(int generation, int sequence) => generation == Generation && sequence <= AppliedPatches;

	public void AppliedGeneration(int generation)
	{
		Generation = generation;
		AppliedPatches = 0;
	}

	public void AppliedPatch(int sequence) => AppliedPatches = sequence;
}
