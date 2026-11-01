namespace BlazorHMR.FullSwap;

/// <summary>
/// One of the assemblies of a new generation, as compiled by the hot reload server.
/// </summary>
/// <param name="Name">Simple assembly name, e.g. "MyApp.Core".</param>
/// <param name="Assembly">The assembly (PE image).</param>
/// <param name="Symbols">Portable PDB, if any.</param>
public sealed record GenerationAssembly(string Name, byte[] Assembly, byte[]? Symbols);
