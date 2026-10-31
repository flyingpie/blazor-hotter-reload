using System.Runtime.Loader;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// Loads Roslyn from the SDK (we only compile against the NuGet packages).
/// <list type="bullet">
/// <item>The compiler comes from the SDK's Roslyn directory: the SDK's source generators (Razor, System.Text.Json, ...) are built against it,
/// and may not load with an older one.</item>
/// <item>The workspace layer and the Edit-and-Continue engine come from the SDK's dotnet-watch, which is built against that same compiler.</item>
/// </list>
/// </summary>
public static class RoslynLoader
{
	/// <summary>
	/// Directory of the SDK's dotnet-watch tool, with Microsoft.CodeAnalysis.Workspaces, .Features, .ExternalAccess.HotReload, System.Composition.
	/// </summary>
	public static string WatchDirectory { get; private set; } = string.Empty;

	/// <summary>
	/// Hooks the default load context, so Roslyn assemblies resolve to the SDK's copies (instead of the NuGet versions we
	/// compiled against). Must be called before the first use of any Roslyn type.
	/// </summary>
	public static void Register(string roslynDirectory)
	{
		if (!File.Exists(Path.Combine(roslynDirectory, "Microsoft.CodeAnalysis.dll")))
		{
			throw new InvalidOperationException($"Roslyn not found in '{roslynDirectory}'.");
		}

		// <sdk>/Roslyn/bincore -> <sdk>/DotnetTools/dotnet-watch/<version>/tools/<tfm>/any
		// There can be multiple versions; take the last one in ordinal order (normally the newest).
		var sdkDirectory = Path.GetFullPath(Path.Combine(roslynDirectory, "..", ".."));
		WatchDirectory =
			Directory
				.EnumerateDirectories(Path.Combine(sdkDirectory, "DotnetTools", "dotnet-watch"), "any", SearchOption.AllDirectories)
				.Where(dir => File.Exists(Path.Combine(dir, "Microsoft.CodeAnalysis.Workspaces.dll")))
				.Order(StringComparer.Ordinal)
				.LastOrDefault()
			?? throw new InvalidOperationException(
				$"dotnet-watch (with Microsoft.CodeAnalysis.Workspaces) not found in SDK '{sdkDirectory}'."
			);

		string[] directories = [roslynDirectory, WatchDirectory];
		AssemblyLoadContext.Default.Resolving += (context, name) =>
		{
			if (name.Name is null || !IsShared(name.Name))
			{
				return null;
			}

			foreach (var directory in directories)
			{
				var path = Path.Combine(directory, name.Name + ".dll");
				if (File.Exists(path))
				{
					return context.LoadFromAssemblyPath(path);
				}
			}

			return null;
		};

		Log.Info($"Using Roslyn from {roslynDirectory}, workspaces from {WatchDirectory}");
	}

	/// <summary>
	/// The assemblies that must come from the SDK for the compiler, workspace, and hot reload service to be binary
	/// compatible (the SDK's source generators are built against those).
	/// </summary>
	private static bool IsShared(string name) =>
		name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
		|| name.StartsWith("System.Composition", StringComparison.Ordinal)
		|| name is "Microsoft.CodeAnalysis.Elfie" or "Humanizer" or "Microsoft.DiaSymReader";
}
