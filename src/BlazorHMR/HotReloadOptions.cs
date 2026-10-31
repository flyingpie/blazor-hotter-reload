using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace BlazorHMR;

/// <summary>
/// Read from the "HotReload" section in appsettings.json (all optional).
/// </summary>
public sealed record HotReloadOptions
{
	/// <summary>
	/// Base URL of the hot reload server.
	/// </summary>
	public Uri ServerUrl { get; init; } = new("http://127.0.0.1:2009/");

	/// <summary>
	/// Old app assemblies cannot be unloaded in the browser, so after this many generations we do a full page reload to free up memory.
	/// </summary>
	public int MaxGenerations { get; init; } = 30;

	/// <summary>
	/// URL of the JS module that talks to the hot reload server. The WebAssembly and desktop packages ship their own copy.
	/// </summary>
	public string ScriptModulePath { get; init; } = "./_content/BlazorHMR/hotreload.js";

	/// <summary>
	/// Reads the optional settings from the "HotReload" configuration section, falling back to the defaults for missing or
	/// invalid values.
	/// </summary>
	[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Makes sure the base URL ends with a slash")]
	public static HotReloadOptions FromConfiguration(IConfiguration config)
	{
		ArgumentNullException.ThrowIfNull(config);

		var section = config.GetSection("HotReload");
		var options = new HotReloadOptions();

		var configuredUrl = section["ServerUrl"];
		if (!string.IsNullOrEmpty(configuredUrl) && Uri.TryCreate(configuredUrl.TrimEnd('/') + '/', UriKind.Absolute, out var url))
		{
			options = options with { ServerUrl = url };
		}

		if (int.TryParse(section["MaxGenerations"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && max > 0)
		{
			options = options with { MaxGenerations = max };
		}

		return options;
	}
}
