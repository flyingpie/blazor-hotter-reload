using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR.Blazor;

/// <summary>
/// Create instances of Razor components, and hydrates them with the state from previous iterations of the same instance.<br/>
/// Stays alive during the entire lifecycle of the running app.
/// </summary>
public sealed class HotReloadComponentActivator(HotReloadRuntime runtime) : IComponentActivator
{
	/// <summary>
	/// Creates an instance of the specified component type and plugs in the state of the previous iteration.<br/>
	/// Throws an exception if the requested type is not a Razor component.
	/// </summary>
	public IComponent CreateInstance(Type componentType)
	{
		ArgumentNullException.ThrowIfNull(componentType);

		// Create instance, and pass along the DI container, so constructor injections works.
		if (ActivatorUtilities.CreateInstance(runtime.Current.Services, componentType) is not IComponent component)
		{
			throw new ArgumentException(
				$"Error creating instance of Razor component: the type {componentType.FullName} does not implement {nameof(IComponent)}.",
				nameof(componentType)
			);
		}

		runtime.PendingState?.Restore(component);

		return component;
	}
}
