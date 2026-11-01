using BlazorHMR.Server.Compilation;

namespace BlazorHMR.Server.Server;

/// <summary>
/// Keeps the assemblies of the last few generations, so a client that reconnects can still download the one it is missing.
/// </summary>
internal sealed class GenerationStore(Lock gate)
{
	// A reconnecting client may be a few generations behind. Keep enough to serve it, but don't keep every assembly forever.
	private const int GenerationsToKeep = 5;

	private readonly Dictionary<int, Dictionary<string, (byte[] Assembly, byte[] Symbols)>> _generations = [];

	public int Current { get; private set; }

	/// <summary>
	/// Stores a new generation and makes it the current one. Returns its number.
	/// </summary>
	public int Add(IReadOnlyDictionary<string, CompileResult> assemblies)
	{
		lock (gate)
		{
			var generation = ++Current;
			_generations[generation] = assemblies.ToDictionary(
				a => a.Key,
				a => (a.Value.Assembly!, a.Value.Symbols!),
				StringComparer.OrdinalIgnoreCase
			);
			_generations.Remove(generation - GenerationsToKeep);
			return generation;
		}
	}

	/// <summary>
	/// Back to the assembly of the build output: forget all generations.
	/// </summary>
	public void Reset()
	{
		lock (gate)
		{
			Current = 0;
			_generations.Clear();
		}
	}

	/// <summary>
	/// The assembly names of a generation, for the hello message.
	/// </summary>
	public IReadOnlyList<string> AssemblyNames(int generation)
	{
		lock (gate)
		{
			return _generations.TryGetValue(generation, out var assemblies) ? [.. assemblies.Keys] : [];
		}
	}

	/// <summary>
	/// The assembly + PDB of a generation, for the client's download endpoint. Null when the generation has been evicted, or
	/// the assembly wasn't part of it.
	/// </summary>
	public (byte[] Assembly, byte[] Symbols)? Get(int generation, string assemblyName)
	{
		lock (gate)
		{
			return _generations.TryGetValue(generation, out var assemblies) && assemblies.TryGetValue(assemblyName, out var value)
				? value
				: null;
		}
	}
}
