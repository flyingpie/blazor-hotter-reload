namespace BlazorHMR;

/// <summary>
/// A compiler diagnostic, as sent by the hot reload server.
/// </summary>
public sealed record HotReloadDiagnostic(string File, int Line, int Column, string Id, string Message)
{
	public override string ToString() => $"{File}({Line},{Column}): {Id}: {Message}";
}
