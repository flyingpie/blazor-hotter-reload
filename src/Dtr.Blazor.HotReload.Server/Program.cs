using System.Globalization;

namespace Dtr.Blazor.HotReload.Server;

public static class Program
{
	private const int DefaultPort = 2009;

	private const string Usage = """
		Usage: dtr-blazor-hotreload [options]

		  --project <path>        The app project (default: the only .csproj in the current directory)
		  --port <port>           Port to listen on (default: 2009)
		  --devserver             Also run (and restart after regular builds) the app's dev server ("dotnet run")
		  --launch-profile <name> Launch profile for the dev server (default: the first one in Properties/launchSettings.json)
		  --devserver-url <url>   Where the dev server listens (default: the launch profile's applicationUrl)
		""";

	public static async Task<int> Main(string[] args)
	{
		// Deliberately simple argument parsing: this is a dev tool, and a full command-line library would be overkill.
		string? project = null;
		var port = DefaultPort;
		var devServer = false;
		Uri? devServerUrl = null;
		string? launchProfile = null;

		var queue = new Queue<string>(args);
		while (queue.TryDequeue(out var arg))
		{
			switch (arg)
			{
				case "--project" when queue.Count > 0:
					project = queue.Dequeue();
					break;
				case "--port" when queue.Count > 0:
					port = int.Parse(queue.Dequeue(), CultureInfo.InvariantCulture);
					break;
				case "--devserver":
					devServer = true;
					break;
				case "--devserver-url" when queue.Count > 0:
					devServerUrl = new Uri(queue.Dequeue());
					break;
				case "--launch-profile" when queue.Count > 0:
					launchProfile = queue.Dequeue();
					break;
				case "-h" or "--help":
					Console.WriteLine(Usage);
					return 0;
				default:
					project ??= arg;
					break;
			}
		}

		project ??= FindProject(Directory.GetCurrentDirectory());
		if (project is null || !File.Exists(project))
		{
			await Console.Error.WriteLineAsync($"Project '{project}' not found, pass it with --project.\n\n{Usage}").ConfigureAwait(false);
			return 1;
		}

		var location = await ProjectLocation.ResolveAsync(Path.GetFullPath(project)).ConfigureAwait(false);
		if (location is null)
		{
			return 1;
		}

		// Needs to happen before anything touches a Roslyn type: it redirects the assembly load context to the SDK's
		// compiler/workspace assemblies.
		RoslynLoader.Register(location.RoslynDirectory);

		DevServerProcess? devServerProcess = null;
		if (devServer)
		{
			devServerUrl ??= LaunchSettings.GetApplicationUrl(location.ProjectDirectory, launchProfile);
			if (devServerUrl is null)
			{
				await Console.Error.WriteLineAsync("No applicationUrl found in Properties/launchSettings.json, pass it with --devserver-url.").ConfigureAwait(false);
				return 1;
			}

			devServerProcess = new DevServerProcess(location, launchProfile, devServerUrl);
		}

		using (devServerProcess)
		{
			await HotReloadServer.RunAsync(location, port, devServerProcess).ConfigureAwait(false);
		}

		return 0;
	}

	/// <summary>
	/// The default project: the only .csproj in the directory. With none or several, the user has to pass --project.
	/// </summary>
	private static string? FindProject(string directory)
	{
		var projects = Directory.GetFiles(directory, "*.csproj");
		return projects.Length == 1 ? projects[0] : null;
	}
}
