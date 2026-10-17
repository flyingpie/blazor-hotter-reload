namespace Dtr.Blazor.HotReload.Server.Deltas;

public enum DeltaStatus
{
	/// <summary>
	/// Nothing that changes the compiled code (e.g. only comments or whitespace).
	/// </summary>
	NoChanges,

	/// <summary>
	/// The changes can be applied in place.
	/// </summary>
	Ready,

	/// <summary>
	/// The changes don't compile.
	/// </summary>
	CompileErrors,

	/// <summary>
	/// The changes compile, but can't be applied in place ("rude edits"), the app needs a full swap.
	/// </summary>
	RestartRequired,
}
