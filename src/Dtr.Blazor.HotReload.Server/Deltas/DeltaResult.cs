using Microsoft.CodeAnalysis;

namespace Dtr.Blazor.HotReload.Server.Deltas;

public sealed record DeltaResult(DeltaStatus Status, IReadOnlyList<DeltaUpdate> Updates, IReadOnlyList<Diagnostic> Diagnostics);
