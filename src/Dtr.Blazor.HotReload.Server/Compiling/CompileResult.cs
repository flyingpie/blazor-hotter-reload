namespace Dtr.Blazor.HotReload.Server.Compiling;

public sealed record CompileResult(byte[]? Assembly, byte[]? Symbols, IReadOnlyList<CompileDiagnostic> Errors, TimeSpan Elapsed)
{
	public bool Success => Assembly is not null;
}
