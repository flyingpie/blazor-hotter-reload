using System.Diagnostics;
using System.Reflection;

namespace BlazorHMR.FullSwap;

/// <summary>
/// Applies a complete new generation of the app: loads the new assemblies next to the old ones, rebuilds the DI container and
/// the component tree, and carries over what state it can. This is the fallback for changes that can't be applied in place
/// (and the only option when the runtime doesn't support in-place patches).
/// </summary>
public sealed class FullSwapApplier(HotReloadRuntime runtime)
{
	/// <summary>
	/// Loads a new version of the app assembly, and swaps the whole app over to it.
	/// Throws when anything goes wrong before the swap; the current generation stays active in that case.
	/// </summary>
	public async Task ApplyAsync(IReadOnlyList<GenerationAssembly> assemblies)
	{
		ArgumentNullException.ThrowIfNull(assemblies);
		ArgumentOutOfRangeException.ThrowIfZero(assemblies.Count);

		using var lease = await runtime.Gate.EnterAsync().ConfigureAwait(true);
		var stopwatch = Stopwatch.StartNew();
		var number = runtime.Current.Number + 1;

		// Build and load the next generation before touching the current one: if anything fails here, the running app is
		// still on the previous generation and keeps working.
		var loadContext = new GenerationLoadContext(number, assemblies);
		var newAssembly = loadContext.Get(runtime.InitialAssembly.GetName().Name!);

		var next = runtime.Generations.Build(number, newAssembly, loadContext.AssembliesByName);
		var previous = runtime.Current;

		// Carry over what state we can: statics and services now, components when they are created (see HotReloadComponentActivator).
		// If an in-place patch left stale code behind, the error boundary may have thrown the components away: use the ones from
		// before that patch.
		var oldComponents = runtime.Recovery.TakeComponentsFor(previous.Number) ?? runtime.Render.GetComponents();
		var state = StateCarryOver.Capture(oldComponents, next.Assemblies);
		try
		{
			state.CarryOverServicesAndStatics(previous, next);
		}
		catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException or MemberAccessException)
		{
			Console.WriteLine($"[hotreload] Could not carry over services/statics: {ex}");
		}

		// From here on, service resolutions and renders use the new generation.
		runtime.SetCurrentGeneration(next);
		runtime.PendingState = state;
		runtime.Render.UpdateCascadingValueSuppliers(next);

		await runtime.Render.RenderRootsAsync().ConfigureAwait(true);

		// The new tree is rendered, so nothing refers to the previous generation's components anymore.
		await previous.DisposeAsync().ConfigureAwait(true);

		stopwatch.Stop();
		var carried = $"{state.Components} components, {state.Services} services, {state.Statics} statics";
		Console.WriteLine(
			$"[hotreload] Applied generation {number} ({string.Join(", ", assemblies.Select(a => a.Name))}) in {stopwatch.ElapsedMilliseconds} ms, "
				+ $"carried over {carried} ({state.Failures} fields could not be carried over)"
		);
		runtime.SetStatus(
			new HotReloadStatus(
				HotReloadStatusKind.Applied,
				$"Hot reload applied, app restarted, state carried over where possible ({carried}; {stopwatch.ElapsedMilliseconds} ms)",
				[]
			)
		);
	}
}
