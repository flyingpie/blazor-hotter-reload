using System.Text.Json;

namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// Finds the libraries (project references) that the app ships to the browser, and gets their compiler command lines.
/// <para>
/// The app's command line comes from its last build (see Dtr.Blazor.HotReload.targets). For the libraries, we do a "design-time" compile:
/// MSBuild runs the compile targets, but instead of compiling, hands us the command line. That's how IDEs do it, and it means the
/// libraries don't need any changes.
/// </para>
/// </summary>
internal static class ProjectGraph
{
	private const string ResponseFileName = "dtr-hotreload.rsp";

	/// <summary>
	/// Projects that must never be hot reloaded (they are part of the hot reload machinery itself).
	/// </summary>
	private static readonly HashSet<string> ExcludedProjects = new(StringComparer.OrdinalIgnoreCase) { "Dtr.Blazor.HotReload.csproj" };

	/// <summary>
	/// Returns the hot projects in dependency order: libraries first, the app last.
	/// </summary>
	public static async Task<IReadOnlyList<HotProject>> LoadAsync(ProjectLocation app)
	{
		var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		var libraries = new Dictionary<string, Library>(StringComparer.Ordinal);

		using (
			var appJson = await DotnetCli
				.MsBuildQueryAsync(
					app.ProjectFile,
					["-getItem:ProjectReference", "-getProperty:AssemblyName", "-getProperty:MSBuildProjectName"]
				)
				.ConfigureAwait(false)
		)
		{
			references[app.ProjectFile] = GetReferences(appJson);
		}

		// Breadth-first, one level at a time (the projects of a level are queried in parallel). The references dictionary
		// doubles as the visited set: only projects that haven't been loaded yet are pending.
		var pending = references[app.ProjectFile].ToList();
		while (pending.Count > 0)
		{
			var loaded = await Task.WhenAll(pending.Select(LoadLibraryAsync)).ConfigureAwait(false);
			foreach (var library in loaded)
			{
				libraries[library.ProjectFile] = library;
				references[library.ProjectFile] = library.References;
			}

			pending = [.. loaded.SelectMany(l => l.References).Distinct(StringComparer.Ordinal).Where(p => !references.ContainsKey(p))];
		}

		// Dependency order (depth-first, post-order): a project is added after all projects it references, so the app is
		// last and libraries can be compiled before their dependents.
		var ordered = new List<HotProject>();
		var visited = new HashSet<string>(StringComparer.Ordinal);
		void Visit(string projectFile)
		{
			if (!visited.Add(projectFile))
			{
				return;
			}

			foreach (var reference in references[projectFile])
			{
				Visit(reference);
			}

			var dependencies = GetTransitiveDependencies(projectFile, references);
			if (projectFile == app.ProjectFile)
			{
				var name = Path.GetFileNameWithoutExtension(app.ProjectFile);
				ordered.Add(
					new HotProject(
						name,
						Path.GetFileNameWithoutExtension(app.IntermediateAssembly),
						app.ProjectFile,
						app.ProjectDirectory,
						app.ResponseFile,
						dependencies,
						IsApp: true
					)
				);
			}
			else
			{
				var library = libraries[projectFile];
				ordered.Add(
					new HotProject(
						library.Name,
						library.AssemblyName,
						projectFile,
						library.Directory,
						library.ResponseFile,
						dependencies,
						IsApp: false
					)
				);
			}
		}

		Visit(app.ProjectFile);
		return ordered;
	}

	/// <summary>
	/// Asks MSBuild for a library's compiler command line (without building it), and writes that as a response file.
	/// </summary>
	private static async Task<Library> LoadLibraryAsync(string projectFile)
	{
		using var json = await DotnetCli
			.MsBuildQueryAsync(
				projectFile,
				[
					// Design-time compile: run the compile targets, but don't actually compile (and don't build referenced projects).
					"-t:Compile",
					"-p:ProvideCommandLineArgs=true",
					"-p:SkipCompilerExecution=true",
					"-p:BuildProjectReferences=false",
					// Missing input file, which makes sure that the CoreCompile target runs, even if the project is up to date.
					"-p:NonExistentFile=__dtr_hotreload__",
					"-getItem:CscCommandLineArgs",
					"-getItem:ProjectReference",
					"-getProperty:AssemblyName",
					"-getProperty:MSBuildProjectName",
					"-getProperty:MSBuildProjectDirectory",
					"-getProperty:IntermediateOutputPath",
				]
			)
			.ConfigureAwait(false);

		var properties = json.RootElement.GetProperty("Properties");
		var directory = properties.GetProperty("MSBuildProjectDirectory").GetString()!;
		var intermediate = DotnetCli.NormalizePath(directory, properties.GetProperty("IntermediateOutputPath").GetString()!);

		var args = json
			.RootElement.GetProperty("Items")
			.GetProperty("CscCommandLineArgs")
			.EnumerateArray()
			.Select(item => item.GetProperty("Identity").GetString()!)
			.ToList();
		if (args.Count == 0)
		{
			throw new InvalidOperationException($"MSBuild did not report a compiler command line for '{projectFile}'.");
		}

		var responseFile = Path.Combine(intermediate, ResponseFileName);
		Directory.CreateDirectory(intermediate);
		await File.WriteAllLinesAsync(responseFile, args).ConfigureAwait(false);

		return new Library(
			projectFile,
			properties.GetProperty("MSBuildProjectName").GetString()!,
			properties.GetProperty("AssemblyName").GetString()!,
			Path.GetFullPath(directory),
			responseFile,
			GetReferences(json)
		);
	}

	/// <summary>
	/// The project references whose output actually ends up in the referencing project. ReferenceOutputAssembly=false
	/// references are build-order-only (e.g. a tool project), and the hot reload machinery itself is excluded.
	/// </summary>
	private static List<string> GetReferences(JsonDocument json)
	{
		if (!json.RootElement.GetProperty("Items").TryGetProperty("ProjectReference", out var items))
		{
			return [];
		}

		return
		[
			.. items
				.EnumerateArray()
				.Where(item =>
					!(
						item.TryGetProperty("ReferenceOutputAssembly", out var output)
						&& string.Equals(output.GetString(), "false", StringComparison.OrdinalIgnoreCase)
					)
				)
				.Select(item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!))
				.Where(path => !ExcludedProjects.Contains(Path.GetFileName(path))),
		];
	}

	/// <summary>
	/// All projects reachable from <paramref name="projectFile"/>, including indirect references (for deciding which
	/// dependents to recompile after a library change).
	/// </summary>
	private static HashSet<string> GetTransitiveDependencies(string projectFile, Dictionary<string, IReadOnlyList<string>> references)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		var stack = new Stack<string>(references[projectFile]);
		while (stack.TryPop(out var current))
		{
			// Add returns false for a project we already saw, which also stops cycles in the project graph.
			if (result.Add(current))
			{
				foreach (var reference in references[current])
				{
					stack.Push(reference);
				}
			}
		}

		return result;
	}

	/// <summary>
	/// What MSBuild told us about one referenced project, before it becomes a <see cref="HotProject"/>.
	/// </summary>
	private sealed record Library(
		string ProjectFile,
		string Name,
		string AssemblyName,
		string Directory,
		string ResponseFile,
		IReadOnlyList<string> References
	);
}
