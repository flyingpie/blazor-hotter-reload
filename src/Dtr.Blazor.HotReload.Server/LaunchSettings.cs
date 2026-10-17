using System.Text.Json;

namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// Reads Properties/launchSettings.json, which "dotnet run" uses too.
/// </summary>
internal static class LaunchSettings
{
	private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	/// <summary>
	/// The first applicationUrl of the given launch profile, or of the first profile that "dotnet run" would use.
	/// </summary>
	public static Uri? GetApplicationUrl(string projectDirectory, string? profileName)
	{
		var path = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
		if (!File.Exists(path))
		{
			return null;
		}

		using var json = JsonDocument.Parse(File.ReadAllText(path), Options);
		if (!json.RootElement.TryGetProperty("profiles", out var profiles))
		{
			return null;
		}

		var profile = profiles
			.EnumerateObject()
			.FirstOrDefault(p =>
				profileName is null
					? p.Value.TryGetProperty("commandName", out var command) && command.GetString() == "Project"
					: p.Name == profileName
			);

		if (profile.Value.ValueKind != JsonValueKind.Object || !profile.Value.TryGetProperty("applicationUrl", out var urls))
		{
			return null;
		}

		var first = urls.GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
		return Uri.TryCreate(first, UriKind.Absolute, out var url) ? url : null;
	}
}
