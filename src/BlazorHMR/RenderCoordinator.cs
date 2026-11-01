using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BlazorHMR.Blazor;
using BlazorHMR.FullSwap;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR;

/// <summary>
/// Owns the renderer-facing part of the runtime: the attached roots, the renderer, re-rendering after a swap, and the
/// renderer's cached cascading value suppliers. Shared by both patch pipelines; only the way the app changes differs.
/// </summary>
[SuppressMessage("Usage", "BL0006:Do not use RenderTree types", Justification = "We need the renderer to re-render and to update its cascading values")]
internal sealed class RenderCoordinator
{
	private const string CascadingValueSupplierTypeName = "Microsoft.AspNetCore.Components.ICascadingValueSupplier";
	private const string CascadingValueSuppliersFieldName = "<ServiceProviderCascadingValueSuppliers>k__BackingField";

	private readonly List<HotReloadRoot> _roots = [];

	/// <summary>
	/// The renderer of the first attached root, or null before the first page attaches.
	/// </summary>
	public Renderer? Renderer { get; private set; }

	/// <summary>
	/// Registers a root. Returns whether this is the first one, which supplies the renderer and starts the server connection.
	/// </summary>
	public bool Attach(HotReloadRoot root, Renderer renderer)
	{
		_roots.Add(root);
		if (Renderer is not null)
		{
			return false;
		}

		Renderer = renderer;

		// Make sure that poking the renderer works, before anyone tries to hot reload anything.
		_ = GetCascadingValueSuppliersField();
		return true;
	}

	public void Detach(HotReloadRoot root) => _roots.Remove(root);

	/// <summary>
	/// A page reload disposes the old tree; the new page attaches new roots.
	/// </summary>
	public void Reset()
	{
		Renderer = null;
		_roots.Clear();
	}

	/// <summary>
	/// The renderer's current components, in creation order, for carrying state over to a new generation.
	/// </summary>
	public List<IComponent> GetComponents() => StateCarryOver.GetComponents(Renderer);

	/// <summary>
	/// Re-renders all roots with the root components of the current generation. Grouped by dispatcher, so each renderer gets
	/// one render tree in one action; resetting the section subscriptions more than once would also unsubscribe the outlets
	/// that the new tree just created.
	/// </summary>
	public async Task RenderRootsAsync()
	{
		foreach (var roots in _roots.ToList().GroupBy(root => root.Dispatcher))
		{
			var dispatcher = roots.Key;
			await dispatcher
				.InvokeAsync(() =>
				{
					SectionSubscriptions.Reset(dispatcher);
					foreach (var root in roots)
					{
						root.RenderGeneration();
					}
				})
				.ConfigureAwait(true);
		}
	}

	/// <summary>
	/// Runs <paramref name="action"/> on the renderer's dispatcher (or directly when no page has attached yet).
	/// </summary>
	public Task InvokeAsync(Action action)
	{
		if (Renderer is { } renderer)
		{
			return renderer.Dispatcher.InvokeAsync(action);
		}

		action();
		return Task.CompletedTask;
	}

	/// <summary>
	/// The renderer caches the cascading values that were registered as services (<c>AddCascadingValue</c>) when it is created.
	/// Point it to the ones of the new generation, otherwise cascading parameters would be fed values of the old types.
	/// </summary>
	public void UpdateCascadingValueSuppliers(AppGeneration generation)
	{
		if (Renderer is null)
		{
			return;
		}

		var supplierType = typeof(CascadingValueSource<>).Assembly.GetType(CascadingValueSupplierTypeName, throwOnError: true)!;
		var suppliers = generation.Services.GetServices(supplierType).ToList();
		var array = Array.CreateInstance(supplierType, suppliers.Count);
		for (var i = 0; i < suppliers.Count; i++)
		{
			array.SetValue(suppliers[i], i);
		}

		GetCascadingValueSuppliersField().SetValue(Renderer, array);
	}

	/// <summary>
	/// The only way to reach the renderer's cascading value suppliers is its private backing field; fail loudly if Blazor changed it.
	/// </summary>
	private static FieldInfo GetCascadingValueSuppliersField() =>
		typeof(Renderer).GetField(CascadingValueSuppliersFieldName, BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException(
			$"Field 'Renderer.{CascadingValueSuppliersFieldName}' was not found; Blazor internals have changed, BlazorHMR needs to be updated."
		);
}
