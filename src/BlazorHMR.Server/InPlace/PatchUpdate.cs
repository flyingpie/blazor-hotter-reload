using BlazorHMR.Protocol;
using Microsoft.CodeAnalysis;

namespace BlazorHMR.Server.InPlace;

/// <summary>
/// Edit-and-Continue delta for one module (assembly). The project id stays on the server (it selects the project for logging
/// and baseline bookkeeping); the rest is what goes over the wire.
/// </summary>
public sealed record PatchUpdate(Guid ModuleId, ProjectId ProjectId, byte[] Metadata, byte[] IL, byte[] Pdb, int[] UpdatedTypes)
{
	public PatchChange ToPatchChange() => new(ModuleId, Metadata, IL, Pdb, UpdatedTypes);
}
