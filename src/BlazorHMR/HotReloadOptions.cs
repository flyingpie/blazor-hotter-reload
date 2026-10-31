namespace BlazorHMR;

/// <summary>
/// Configuration for hot reload.<br/>
/// TODO(MvdO): Use env vars.
/// </summary>
public static class HotReloadOptions
{
	/// <summary>
	/// Base URL of the hot reload server.
	/// </summary>
	public static Uri ServerUrl => new("http://127.0.0.1:2009/");

	/// <summary>
	/// Old app assemblies cannot be unloaded in the browser, so after this many generations we do a full page reload to free up memory.
	/// </summary>
	public static int MaxGenerations => 30;
}
