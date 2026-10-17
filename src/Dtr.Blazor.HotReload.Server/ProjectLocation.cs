namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// Where the files are that the build of the app project (with the build targets of the Dtr.Blazor.HotReload package) left for us.
/// </summary>
public sealed record ProjectLocation(
	string ProjectFile,
	string ProjectDirectory,
	string ResponseFile,
	string IntermediateAssembly,
	string RoslynDirectory
)
{
	public static async Task<ProjectLocation?> ResolveAsync(string projectFile)
	{
		var intermediateDir = await GetIntermediateDirectoryAsync(projectFile).ConfigureAwait(false);
		var infoFile = Path.Combine(intermediateDir, "dtr-hotreload.txt");
		var responseFile = Path.Combine(intermediateDir, "dtr-hotreload.rsp");

		if (!File.Exists(infoFile) || !File.Exists(responseFile))
		{
			Log.Info("No hot reload info found from a previous build, building the project first...");
			if (await DotnetCli.RunAsync(["build", projectFile, "-c", "Debug"], echo: true).ConfigureAwait(false) is not (0, _))
			{
				Log.Error("Build failed.");
				return null;
			}

			if (!File.Exists(infoFile))
			{
				Log.Error($"'{infoFile}' still not found; does the project reference the Dtr.Blazor.HotReload package, and is it built in Debug?");
				return null;
			}
		}

		var info = (await File.ReadAllLinesAsync(infoFile).ConfigureAwait(false))
			.Select(line => line.Split('=', 2))
			.Where(parts => parts.Length == 2)
			.ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

		return new ProjectLocation(
			projectFile,
			Path.GetFullPath(info["ProjectDir"]),
			responseFile,
			Path.GetFullPath(info["IntermediateAssembly"]),
			Path.GetFullPath(Path.Combine(info["RoslynPath"], "bincore"))
		);
	}

	private static async Task<string> GetIntermediateDirectoryAsync(string projectFile)
	{
		using var json = await DotnetCli
			.MsBuildQueryAsync(projectFile, ["-getProperty:MSBuildProjectDirectory", "-getProperty:IntermediateOutputPath"])
			.ConfigureAwait(false);

		var properties = json.RootElement.GetProperty("Properties");
		return DotnetCli.NormalizePath(
			properties.GetProperty("MSBuildProjectDirectory").GetString()!,
			properties.GetProperty("IntermediateOutputPath").GetString()!
		);
	}
}
