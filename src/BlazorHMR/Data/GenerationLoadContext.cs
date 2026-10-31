using System.Reflection;
using System.Runtime.Loader;

namespace BlazorHMR;

/// <summary>
/// Holds the assemblies of one generation: the new app assembly, plus the libraries that changed since the last build.
/// <para>
/// These have the same names as the assemblies the page started with, so they need their own load context. Within the generation,
/// references to those names resolve to the new versions (so the new app uses the new MyApp.Core). Everything else (the framework,
/// third-party libraries, unchanged libraries) comes from the default context, so those types are shared with the host tier.
/// </para>
/// </summary>
public sealed class GenerationLoadContext : AssemblyLoadContext
{
	private readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Loads the generation's assemblies from memory. They are kept in the dictionary, so later lookups (and references from
	/// the app back to itself) resolve to the same instance. The context is named after the generation to make stack traces
	/// and diagnostics readable.
	/// </summary>
	public GenerationLoadContext(int number, IEnumerable<GenerationAssembly> assemblies)
		: base($"hotreload-{number}")
	{
		foreach (var assembly in assemblies)
		{
			using var assemblyStream = new MemoryStream(assembly.Assembly);
			using var symbolStream = assembly.Symbols is null ? null : new MemoryStream(assembly.Symbols);
			_assemblies[assembly.Name] = LoadFromStream(assemblyStream, symbolStream);
		}
	}

	public IReadOnlyDictionary<string, Assembly> AssembliesByName => _assemblies;

	public Assembly Get(string name) =>
		_assemblies.TryGetValue(name, out var assembly)
			? assembly
			: throw new InvalidOperationException($"Assembly '{name}' is not part of this generation.");

	/// <summary>
	/// Resolves names that are part of the generation; everything else falls through to the default context (the framework,
	/// third-party packages, unchanged libraries), so those types stay shared with the host tier.
	/// </summary>
	protected override Assembly? Load(AssemblyName assemblyName) =>
		assemblyName.Name is not null && _assemblies.TryGetValue(assemblyName.Name, out var assembly) ? assembly : null;
}
