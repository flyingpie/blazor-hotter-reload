namespace Dtr.Blazor.HotReload;

/// <summary>
/// What the overlay shows.
/// </summary>
public sealed record HotReloadStatus(HotReloadStatusKind Kind, string Message, IReadOnlyList<string> Details)
{
	public bool IsError => Kind is HotReloadStatusKind.CompileErrors or HotReloadStatusKind.Failed;
}
