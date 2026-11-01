using System.Diagnostics;
using BlazorHMR.Blazor;
using BlazorHMR.Protocol;

namespace BlazorHMR.InPlace;

/// <summary>
/// Applies Edit-and-Continue patches to the running assemblies: nothing is recreated, so all state is kept. Blazor re-renders
/// all components afterwards. When a patch leaves stale code behind, the caller asks for a full swap instead.
/// </summary>
public sealed class InPlacePatchApplier(HotReloadRuntime runtime)
{
	/// <summary>
	/// Patches the running assemblies in place.
	/// </summary>
	public async Task ApplyAsync(IReadOnlyList<PatchChange> patches)
	{
		ArgumentNullException.ThrowIfNull(patches);

		using var lease = await runtime.Gate.EnterAsync().ConfigureAwait(true);
		var stopwatch = Stopwatch.StartNew();

		// In case the patch leaves stale code behind, and a full swap is needed after all (see OnUnhandledException):
		// keep a snapshot of the current component tree. By the time the swap happens, the error boundary may have thrown
		// parts of it away, but this snapshot still has them.
		runtime.Recovery.ComponentsBeforePatch = runtime.Render.GetComponents();

		// The metadata handlers re-render on the dispatcher. Reset the section subscriptions there, immediately before that
		// render: the diff may create the new outlet before disposing the old one, and Blazor allows only one per section ID.
		await runtime
			.Render.InvokeAsync(() =>
			{
				if (runtime.Render.Renderer is { } renderer)
				{
					var dispatcher = renderer.Dispatcher;
					PatchApplier.Apply(patches, () => SectionSubscriptions.Reset(dispatcher));
				}
				else
				{
					PatchApplier.Apply(patches);
				}
			})
			.ConfigureAwait(true);

		stopwatch.Stop();

		Console.WriteLine($"[hotreload] Applied patch in place ({patches.Count} assemblies) in {stopwatch.ElapsedMilliseconds} ms");
		runtime.SetStatus(
			new HotReloadStatus(
				HotReloadStatusKind.Applied,
				$"Hot reload applied in place, state kept ({stopwatch.ElapsedMilliseconds} ms)",
				[]
			)
		);
	}
}
