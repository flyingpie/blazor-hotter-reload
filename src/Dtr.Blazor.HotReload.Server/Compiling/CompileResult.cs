namespace Dtr.Blazor.HotReload.Server.Compiling;

/// <summary>
/// The output of compiling one project: an in-memory assembly and portable PDB, or the errors that stopped it.
/// </summary>
/// <param name="Assembly">The PE image, or null when compilation failed.</param>
/// <param name="Symbols">The portable PDB, needed for the next in-place update.</param>
/// <param name="Errors">The compiler errors (empty on success).</param>
/// <param name="Elapsed">How long the compile took, for the log.</param>
public sealed record CompileResult(byte[]? Assembly, byte[]? Symbols, IReadOnlyList<CompileDiagnostic> Errors, TimeSpan Elapsed)
{
	public bool Success => Assembly is not null;
}
