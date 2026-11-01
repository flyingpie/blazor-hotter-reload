using Microsoft.CodeAnalysis;

namespace BlazorHMR.Server.InPlace;

/// <summary>
/// The outcome of asking Roslyn for an in-place update: whether it can be applied, the per-module deltas, and any diagnostics
/// that explain why it can't.
/// </summary>
public sealed record PatchResult(PatchStatus Status, IReadOnlyList<PatchUpdate> Updates, IReadOnlyList<Diagnostic> Diagnostics);
