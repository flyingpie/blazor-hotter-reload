namespace BlazorHMR.Server.Compilation;

/// <summary>
/// Decides whether a batch of changes should try the in-place pipeline first, or go straight to a full swap.
/// </summary>
internal static class PatchPlanner
{
	/// <summary>
	/// In-place patches keep all state, so prefer them whenever the runtime supports them. Two things rule them out:
	/// the client asked for a full swap, or the change touches service registrations (which are only rebuilt by a full swap).
	/// </summary>
	public static bool ShouldTryInPlace(bool fullSwapRequested, bool supportsInPlace, IEnumerable<string> changedFiles) =>
		!fullSwapRequested && supportsInPlace && !TouchesServiceRegistrations(changedFiles);

	/// <summary>
	/// Changes to the service registrations can be applied in place, but that doesn't rebuild the DI container, so they need a full swap.
	/// </summary>
	private static bool TouchesServiceRegistrations(IEnumerable<string> files) =>
		files.Any(file =>
			Path.GetExtension(file) == ".cs"
			&& File.Exists(file)
			&& File.ReadAllText(file).Contains("IServiceCollection", StringComparison.Ordinal)
		);
}
