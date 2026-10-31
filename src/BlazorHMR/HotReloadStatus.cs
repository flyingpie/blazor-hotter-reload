namespace BlazorHMR;

/// <summary>
/// What the overlay shows.
/// </summary>
public sealed record HotReloadStatus(HotReloadStatusKind Kind, string Message, IReadOnlyList<string> Details)
{
	/// <summary>
	/// Whether this status represents an error (compile errors, or a failed apply), as opposed to progress.
	/// </summary>
	public bool IsError => Kind is HotReloadStatusKind.CompileErrors or HotReloadStatusKind.Failed;
}
