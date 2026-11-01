namespace BlazorHMR.Protocol;

/// <summary>
/// The <c>type</c> of every message on the hot reload WebSocket. One place for the wire names, so the client and the server
/// can't drift apart.
/// </summary>
public static class MessageTypes
{
	// Server -> client.

	/// <summary>
	/// The server's current state, sent when a client connects (and again after a reconnect so it can catch up).
	/// </summary>
	public const string Hello = "hello";

	/// <summary>
	/// A compile is running; the overlay shows progress.
	/// </summary>
	public const string Compiling = "compiling";

	/// <summary>
	/// The changes didn't affect the compiled code, so there is nothing to apply.
	/// </summary>
	public const string NoChanges = "nochanges";

	/// <summary>
	/// A complete new generation of the app is available for a full swap.
	/// </summary>
	public const string Generation = "generation";

	/// <summary>
	/// An in-place patch for the current generation.
	/// </summary>
	public const string Patch = "patch";

	/// <summary>
	/// The code doesn't compile; the payload holds the diagnostics.
	/// </summary>
	public const string Errors = "errors";

	/// <summary>
	/// A stylesheet changed and has to be reloaded.
	/// </summary>
	public const string Css = "css";

	/// <summary>
	/// The app was rebuilt outside the hot reload server; the page has to start over.
	/// </summary>
	public const string Rebuilt = "rebuilt";

	/// <summary>
	/// The page has to reload.
	/// </summary>
	public const string Reload = "reload";

	// Client -> server.

	/// <summary>
	/// What kind of in-place patches the client's runtime can apply.
	/// </summary>
	public const string Capabilities = "capabilities";

	/// <summary>
	/// The client asks for a full swap (a new generation, compiled from scratch).
	/// </summary>
	public const string Restart = "restart";

	/// <summary>
	/// The client couldn't apply an in-place patch; the server should do a full swap instead.
	/// </summary>
	public const string PatchFailed = "patch-failed";
}
