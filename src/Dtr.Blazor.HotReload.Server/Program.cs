using System.Globalization;

namespace Dtr.Blazor.HotReload.Server;

public static class Program
{
	private const int DefaultPort = 2009;

	private const string Usage = """
		Usage: dtr-blazor-hotreload [options]

		  --project <path>        The app project (default: the only .csproj in the current directory)
		  --port <port>           Port to listen on (default: 2009)
		""";

	public static async Task<int> Main(string[] args)
	{
		// Deliberately simple argument parsing: this is a dev tool, and a full command-line library would be overkill.
		string? project = null;
		var port = DefaultPort;

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

		await HotReloadServer.RunAsync(location, port).ConfigureAwait(false);
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
