using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorHMR.Blazor;

/// <summary>
/// Takes the place of the app's root component (e.g. <c>App</c>), and renders the root component of the current app generation.
/// Every generation gets a freshly keyed subtree, so Blazor disposes the old components and creates new ones.
/// The hot reload status, fresh-start button, and error panel come from <see cref="HotReloadOverlay"/>.
/// </summary>
public class HotReloadRoot : IComponent, IDisposable
{
	private RenderHandle _renderHandle;
	private bool _initialized;
	private bool _ownsOverlay;

	/// <summary>
	/// Full name of the root component type, looked up in the assembly of every generation.
	/// </summary>
	[Parameter]
	[EditorRequired]
	public string RootTypeName { get; set; } = null!;

	[Inject]
	private HotReloadRuntime Runtime { get; set; } = null!;

	/// <summary>
	/// Blazor calls this once when the component is attached to a renderer.
	/// </summary>
	public void Attach(RenderHandle renderHandle) => _renderHandle = renderHandle;

	public Task SetParametersAsync(ParameterView parameters)
	{
		parameters.SetParameterProperties(this);

		if (!_initialized)
		{
			_initialized = true;

			// With multiple app roots (e.g. several selectors), only the first one owns the overlay. All roots still attach,
			// so the runtime re-renders every root after a swap.
			_ownsOverlay = !Runtime.HasOverlayOwner;
			Runtime.HasOverlayOwner = true;
			Runtime.Attach(this, GetRenderer(_renderHandle));
		}

		Render();
		return Task.CompletedTask;
	}

	/// <summary>
	/// The page is going away, so detach from the runtime. On the desktop the runtime itself keeps running across page reloads.
	/// </summary>
	public void Dispose()
	{
		Runtime.Detach(this);
		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// The dispatcher of the renderer this root is attached to. The runtime uses it to reset the section subscriptions and render
	/// in one action.
	/// </summary>
	public Dispatcher Dispatcher => _renderHandle.Dispatcher;

	/// <summary>
	/// Called by the runtime after a generation swap, on the renderer's dispatcher.
	/// </summary>
	public void RenderGeneration() => Render();

	/// <summary>
	/// <see cref="RenderHandle"/> doesn't expose its renderer, but the runtime needs it to find and replace components.
	/// </summary>
	private static Renderer GetRenderer(RenderHandle handle)
	{
		var field =
			typeof(RenderHandle).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(
				"Field 'RenderHandle._renderer' was not found; Blazor internals have changed, BlazorHMR needs to be updated."
			);

		return (Renderer)field.GetValue(handle)!;
	}

	private void Render() => _renderHandle.Render(BuildRenderTree);

	/// <summary>
	/// Renders the current generation's root inside an error boundary. The key changes with the generation, so Blazor treats
	/// it as a new subtree: the old components are disposed and the new ones are created (and get carried-over state).
	/// </summary>
	private void BuildRenderTree(RenderTreeBuilder builder)
	{
		var generation = Runtime.Current;
		var rootType = Runtime.GetRootType(RootTypeName);

		builder.OpenComponent<ErrorBoundary>(0);
		builder.SetKey(generation.Number);
		builder.AddComponentParameter(
			1,
			nameof(ErrorBoundary.ChildContent),
			(RenderFragment)(
				b =>
				{
					b.OpenComponent(0, rootType);
					b.CloseComponent();
				}
			)
		);
		builder.AddComponentParameter(2, nameof(ErrorBoundary.ErrorContent), (RenderFragment<Exception>)BuildErrorContent);
		builder.CloseComponent();

		// The overlay lives outside the keyed subtree, so it survives a generation swap and keeps its status subscription.
		if (_ownsOverlay)
		{
			builder.OpenComponent<HotReloadOverlay>(10);
			builder.CloseComponent();
		}
	}

	/// <summary>
	/// The error boundary's error panel: the same overlay component, with the exception it caught.
	/// </summary>
	private static RenderFragment BuildErrorContent(Exception exception) =>
		builder =>
		{
			builder.OpenComponent<HotReloadOverlay>(0);
			builder.AddComponentParameter(1, nameof(HotReloadOverlay.Exception), exception);
			builder.CloseComponent();
		};
}

/// <summary>
/// Desktop hosts cannot pass root component parameters (Photino's root component list only holds a type and a selector),
/// so the root type name is baked in as a generic argument instead. Only the name is used; the type itself is looked up in every
/// generation, so it doesn't matter that the generic argument is the type from the first generation.
/// </summary>
public sealed class HotReloadRoot<T> : HotReloadRoot
	where T : IComponent
{
	public HotReloadRoot() => RootTypeName = typeof(T).FullName!;
}
