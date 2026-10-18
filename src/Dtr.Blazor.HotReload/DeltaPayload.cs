namespace Dtr.Blazor.HotReload;

/// <summary>
/// An in-place update (Edit-and-Continue delta) for one loaded assembly, as computed by the hot reload server.
/// </summary>
/// <param name="ModuleId">The MVID of the assembly to update.</param>
/// <param name="Metadata">Metadata delta.</param>
/// <param name="IL">IL delta.</param>
/// <param name="Pdb">PDB delta.</param>
/// <param name="UpdatedTypes">Metadata tokens of the types that changed.</param>
public sealed record DeltaPayload(Guid ModuleId, byte[] Metadata, byte[] IL, byte[] Pdb, int[] UpdatedTypes);
