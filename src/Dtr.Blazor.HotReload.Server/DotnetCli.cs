using System.Diagnostics;
using System.Text.Json;

namespace Dtr.Blazor.HotReload.Server;

internal static class DotnetCli
{
	/// <summary>
	/// Runs "dotnet" with the given arguments. With <paramref name="echo"/>, the output goes to the console, otherwise it's returned.
	/// </summary>
	public static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> args, bool echo)
	{
		var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = !echo, UseShellExecute = false };
		foreach (var arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start 'dotnet'.");
		var output = echo ? string.Empty : await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
		await process.WaitForExitAsync().ConfigureAwait(false);
		return (process.ExitCode, output);
	}

	/// <summary>
	/// Runs "dotnet msbuild" with -getProperty/-getItem arguments, and returns the resulting JSON.
	/// </summary>
	public static async Task<JsonDocument> MsBuildQueryAsync(string projectFile, IEnumerable<string> args)
	{
		var (exitCode, output) = await RunAsync(["msbuild", projectFile, "-p:Configuration=Debug", .. args], echo: false)
			.ConfigureAwait(false);
		if (exitCode != 0)
		{
			throw new InvalidOperationException($"Could not evaluate '{projectFile}':\n{output}");
		}

		return JsonDocument.Parse(output);
	}

	/// <summary>
	/// MSBuild paths can contain backslashes, even on Linux.
	/// </summary>
	public static string NormalizePath(string baseDirectory, string path) =>
		Path.GetFullPath(Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? path : path.Replace('\\', '/')));
}
