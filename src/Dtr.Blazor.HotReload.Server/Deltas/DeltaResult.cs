using Microsoft.CodeAnalysis;

namespace Dtr.Blazor.HotReload.Server.Deltas;

/// <summary>
/// The outcome of asking Roslyn for an in-place update: whether it can be applied, the per-module deltas, and any diagnostics
/// that explain why it can't.
/// </summary>
public sealed record DeltaResult(DeltaStatus Status, IReadOnlyList<DeltaUpdate> Updates, IReadOnlyList<Diagnostic> Diagnostics);
