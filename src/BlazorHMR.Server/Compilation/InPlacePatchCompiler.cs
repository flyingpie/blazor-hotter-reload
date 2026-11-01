using BlazorHMR.Server.InPlace;
using BlazorHMR.Server.Project;
using Microsoft.CodeAnalysis;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// The outcome of asking Roslyn for in-place patches. <see cref="Status"/> is null when the runtime doesn't support them at
/// all (the internal Roslyn service is missing), so the caller goes straight to a full swap.
/// </summary>
internal sealed record InPlaceCompileResult(PatchStatus? Status, IReadOnlyList<PatchUpdate> Changes, IReadOnlyList<Diagnostic> Diagnostics)
{
	public static readonly InPlaceCompileResult Unavailable = new(null, [], []);
}

/// <summary>
/// Owns the Edit-and-Continue session: the capabilities of the connected client, and the Roslyn hot reload service that
/// computes in-place patches. Keeps the full-swap pipeline free of delta details.
/// </summary>
internal sealed class InPlacePatchCompiler
{
	private PatchSession? _session;

	/// <summary>
	/// Whether the Roslyn service exists in this SDK (if not, every change becomes a full swap).
	/// </summary>
	public bool IsAvailable => _session is not null;

	/// <summary>
	/// Whether the connected client told us what its runtime can apply.
	/// </summary>
	public bool HasCapabilities => _session is { HasCapabilities: true };

	/// <summary>
	/// (Re)creates the session from the current solution. The client runs the build output, which is the baseline.
	/// </summary>
	public async Task ResetAsync(HotSolution solution, string? capabilities)
	{
		_session = PatchSession.TryCreate(solution.Solution.Services);
		if (_session is null)
		{
			return;
		}

		if (capabilities is not null)
		{
			_session.SetCapabilities(capabilities);
		}

		await _session.StartAsync(solution.Solution).ConfigureAwait(false);
	}

	/// <summary>
	/// Tells Roslyn what the client's runtime can apply. Must run on the worker thread (the Roslyn service isn't thread-safe).
	/// </summary>
	public void SetCapabilities(string capabilities) => _session?.SetCapabilities(capabilities);

	/// <summary>
	/// Asks Roslyn for the changes since the baseline. A ready result is committed, so the solution becomes the next baseline.
	/// </summary>
	public async Task<InPlaceCompileResult> CompileAsync(Solution solution, IEnumerable<ProjectId> runningProjects)
	{
		if (_session is not { HasCapabilities: true } session)
		{
			return InPlaceCompileResult.Unavailable;
		}

		var result = await session.GetUpdatesAsync(solution, runningProjects).ConfigureAwait(false);
		if (result.Status == PatchStatus.Ready)
		{
			session.Commit();
		}

		return new InPlaceCompileResult(result.Status, result.Updates, result.Diagnostics);
	}

	/// <summary>
	/// After a full swap: the new generation is the baseline for the next in-place patches.
	/// </summary>
	public async Task StartAsync(Solution solution)
	{
		if (_session is not null)
		{
			await _session.StartAsync(solution).ConfigureAwait(false);
		}
	}
}
