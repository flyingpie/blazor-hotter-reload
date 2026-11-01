namespace BlazorHMR.Protocol;

/// <summary>
/// A compiler diagnostic, as sent by the hot reload server (line and column are 1-based).
/// </summary>
public sealed record CompileDiagnostic(string File, int Line, int Column, string Id, string Message)
{
	public override string ToString() => $"{File}({Line},{Column}): {Id}: {Message}";
}
