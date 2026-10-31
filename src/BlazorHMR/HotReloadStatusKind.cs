namespace BlazorHMR;

public enum HotReloadStatusKind
{
	/// <summary>
	/// A new generation is running.
	/// </summary>
	Applied,

	/// <summary>
	/// The server is compiling a change.
	/// </summary>
	Compiling,

	/// <summary>
	/// The last change did not compile; the previous generation is still running.
	/// </summary>
	CompileErrors,

	/// <summary>
	/// Something else went wrong (loading the assembly, building the services, ...).
	/// </summary>
	Failed,
}
