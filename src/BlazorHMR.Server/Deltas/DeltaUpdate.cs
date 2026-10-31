using Microsoft.CodeAnalysis;

namespace BlazorHMR.Server.Deltas;

/// <summary>
/// Edit-and-Continue delta for one module (assembly).
/// </summary>
public sealed record DeltaUpdate(Guid ModuleId, ProjectId ProjectId, byte[] Metadata, byte[] IL, byte[] Pdb, int[] UpdatedTypes);
