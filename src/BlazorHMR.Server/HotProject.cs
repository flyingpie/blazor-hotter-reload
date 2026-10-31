namespace BlazorHMR.Server;

/// <summary>
/// A project whose assembly is hot reloaded: the app itself, or one of the libraries it ships.
/// </summary>
/// <param name="Name">The project name, e.g. "Core".</param>
/// <param name="AssemblyName">E.g. "MyApp.Core".</param>
/// <param name="ProjectFile">Full path of the .csproj.</param>
/// <param name="ProjectDirectory">Directory of the .csproj.</param>
/// <param name="ResponseFile">The compiler command line for this project.</param>
/// <param name="Dependencies">Project files of all (transitively) referenced hot projects.</param>
/// <param name="IsApp">Whether this is the app project.</param>
public sealed record HotProject(
	string Name,
	string AssemblyName,
	string ProjectFile,
	string ProjectDirectory,
	string ResponseFile,
	IReadOnlySet<string> Dependencies,
	bool IsApp
);
