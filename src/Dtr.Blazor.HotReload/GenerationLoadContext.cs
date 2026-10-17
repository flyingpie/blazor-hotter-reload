using System.Reflection;
using System.Runtime.Loader;

namespace Dtr.Blazor.HotReload;

/// <summary>
/// Holds the assemblies of one generation: the new app assembly, plus the libraries that changed since the last build.
/// <para>
/// These have the same names as the assemblies the page started with, so they need their own load context. Within the generation,
/// references to those names resolve to the new versions (so the new app uses the new Dtr.Core). Everything else (the framework,
/// third-party libraries, unchanged libraries) comes from the default context, so those types are shared with the host tier.
/// </para>
/// </summary>
internal sealed class GenerationLoadContext : AssemblyLoadContext
{
	private readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);

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

	protected override Assembly? Load(AssemblyName assemblyName) =>
		assemblyName.Name is not null && _assemblies.TryGetValue(assemblyName.Name, out var assembly) ? assembly : null;
}
