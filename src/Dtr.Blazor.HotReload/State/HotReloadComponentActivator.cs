using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dtr.Blazor.HotReload.State;

/// <summary>
/// Creates components like Blazor's default activator does, and hands new components the state of their predecessors after a full swap.
/// Registered in the host tier, so the renderer uses it for every generation.
/// </summary>
internal sealed class HotReloadComponentActivator(HotReloadRuntime runtime) : IComponentActivator
{
	/// <summary>
	/// Creates the component, then (after a full swap) copies in the state of the matching component from the previous
	/// generation. This happens before Blazor sets parameters and calls the lifecycle methods.
	/// </summary>
	public IComponent CreateInstance(Type componentType)
	{
		ArgumentNullException.ThrowIfNull(componentType);

		// Like Blazor's default activator: supports constructor injection (from the current generation's services).
		if (ActivatorUtilities.CreateInstance(runtime.Current.Services, componentType) is not IComponent component)
		{
			throw new ArgumentException(
				$"The type {componentType.FullName} does not implement {nameof(IComponent)}.",
				nameof(componentType)
			);
		}

		runtime.PendingState?.Restore(component);
		return component;
	}
}
