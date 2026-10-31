using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BlazorHMR.Server.Project;

/// <summary>
/// Everything the compiler needs, taken from the command line that MSBuild used for the last real build.
/// <para>
/// The source files (.cs) and Razor files in the project directory are not taken from the command line, but from the file system,
/// so files can be added and removed. Everything else (references, generated files under obj/, generators, editorconfigs) is fixed
/// until the next real build.
/// </para>
/// </summary>
internal sealed class ProjectInputs
{
	private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

	private ProjectInputs(string projectDirectory, CSharpCommandLineArguments arguments, ImmutableArray<string> fixedSourceFiles)
	{
		ProjectDirectory = projectDirectory;
		Arguments = arguments;
		FixedSourceFiles = fixedSourceFiles;
	}

	public string ProjectDirectory { get; }

	public CSharpCommandLineArguments Arguments { get; }

	/// <summary>
	/// The assembly that the build writes (the "/out" argument).
	/// </summary>
	public string OutputAssembly => Path.Combine(Arguments.OutputDirectory, Arguments.OutputFileName ?? string.Empty);

	/// <summary>
	/// Sources from outside the project directory (e.g. UniversalUsings.cs) or under obj/ (GlobalUsings.g.cs, AssemblyInfo.cs, ...).
	/// </summary>
	public ImmutableArray<string> FixedSourceFiles { get; }

	/// <summary>
	/// Parses the response file and splits its source files into "live" (watched, reloaded from disk) and "fixed"
	/// (generated or outside the project, so they only change with a real build).
	/// </summary>
	public static ProjectInputs Load(string responseFile, string projectDirectory)
	{
		var arguments = CSharpCommandLineParser.Default.Parse(["@" + responseFile], projectDirectory, sdkDirectory: null);

		var errors = arguments.Errors.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
		if (errors.Count > 0)
		{
			throw new InvalidOperationException($"Could not parse '{responseFile}':\n{string.Join('\n', errors)}");
		}

		var fixedSources = arguments
			.SourceFiles.Select(f => Path.GetFullPath(f.Path, projectDirectory))
			.Where(path => !IsLiveFile(projectDirectory, path))
			.ToImmutableArray();

		return new ProjectInputs(projectDirectory, arguments, fixedSources);
	}

	/// <summary>
	/// A live file is a C#/Razor file inside the project directory (not under bin/obj), i.e. one that the user edits and that
	/// the watcher can add, change, or remove without a real build.
	/// </summary>
	public static bool IsLiveFile(string projectDirectory, string path)
	{
		var extension = Path.GetExtension(path);
		if (extension is not (".cs" or ".razor" or ".cshtml"))
		{
			return false;
		}

		var relative = Path.GetRelativePath(projectDirectory, path);
		return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && !IsExcludedDirectory(relative);
	}

	/// <summary>
	/// All C# files: the fixed ones, plus the current ones in the project directory.
	/// </summary>
	public IEnumerable<string> GetSourceFiles() => FixedSourceFiles.Concat(EnumerateLiveFiles("*.cs"));

	/// <summary>
	/// Additional files for the generators: the fixed ones (non-Razor), plus the current Razor files in the project directory.
	/// </summary>
	public IEnumerable<string> GetAdditionalFiles() =>
		Arguments
			.AdditionalFiles.Select(f => Path.GetFullPath(f.Path, ProjectDirectory))
			.Where(path => !IsLiveFile(ProjectDirectory, path))
			.Concat(EnumerateLiveFiles("*.razor"))
			.Concat(EnumerateLiveFiles("*.cshtml"));

	private static bool IsExcludedDirectory(string relativePath)
	{
		var first = relativePath.Split(PathSeparators)[0];

		// bin/obj: build output; node_modules and hidden directories: not part of the project.
		return first is "bin" or "obj" or "node_modules" || first.StartsWith('.');
	}

	/// <summary>
	/// Live files of one kind, in a stable order (so the generated code and diagnostics don't shuffle between compiles).
	/// </summary>
	private IEnumerable<string> EnumerateLiveFiles(string pattern) =>
		Directory
			.EnumerateFiles(ProjectDirectory, pattern, SearchOption.AllDirectories)
			.Where(path => IsLiveFile(ProjectDirectory, path))
			.Order(StringComparer.Ordinal);
}
