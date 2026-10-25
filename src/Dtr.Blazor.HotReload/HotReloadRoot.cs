using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;

namespace Dtr.Blazor.HotReload;

/// <summary>
/// Takes the place of the app's root component (e.g. <c>App</c>), and renders the root component of the current app generation.
/// Every generation gets a freshly keyed subtree, so Blazor disposes the old components and creates new ones.
/// Also renders a small overlay with hot reload status / compiler errors.
/// </summary>
[SuppressMessage(
	"Major Code Smell",
	"S3011:Reflection should not be used to increase accessibility",
	Justification = "Dev-only tooling, poking Blazor internals is the point"
)]
[SuppressMessage("Usage", "BL0006:Do not use RenderTree types", Justification = "We need the renderer to update its cascading values")]
public class HotReloadRoot : IComponent, IDisposable
{
	private const string FreshStartTitle = "Hot reload: fresh start (recompile everything and reload the page)";

	private static readonly TimeSpan AppliedStatusDuration = TimeSpan.FromSeconds(3);

	private RenderHandle _renderHandle;
	private bool _initialized;
	private bool _ownsOverlay;
	private HotReloadStatus? _dismissedStatus;
	private CancellationTokenSource? _hideStatus;

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
			Runtime.StatusChanged += OnStatusChanged;
			Runtime.Attach(this, GetRenderer(_renderHandle));
		}

		Render();
		return Task.CompletedTask;
	}

	/// <summary>
	/// The page is going away, so stop listening. On the desktop the runtime itself keeps running across page reloads.
	/// </summary>
	public void Dispose()
	{
		Runtime.StatusChanged -= OnStatusChanged;
		Runtime.Detach(this);
		_hideStatus?.Cancel();
		_hideStatus?.Dispose();
		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// The dispatcher of the renderer this root is attached to. The runtime uses it to reset the section subscriptions and render
	/// in one action.
	/// </summary>
	internal Dispatcher Dispatcher => _renderHandle.Dispatcher;

	/// <summary>
	/// Called by the runtime after a generation swap, on the renderer's dispatcher.
	/// </summary>
	internal void RenderGeneration() => Render();

	/// <summary>
	/// <see cref="RenderHandle"/> doesn't expose its renderer, but the runtime needs it to find and replace components.
	/// </summary>
	private static Renderer GetRenderer(RenderHandle handle)
	{
		var field =
			typeof(RenderHandle).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(
				"Field 'RenderHandle._renderer' was not found; Blazor internals have changed, Dtr.Blazor.HotReload needs to be updated."
			);

		return (Renderer)field.GetValue(handle)!;
	}

	/// <summary>
	/// Replaces the app with a readable error and a way out. The runtime uses the exception to detect stale code after an
	/// in-place update and to request a full swap.
	/// </summary>
	private void BuildErrorContent(RenderTreeBuilder builder, Exception exception)
	{
		Runtime.OnUnhandledException(exception);

		builder.OpenElement(0, "div");
		builder.AddAttribute(
			1,
			"style",
			"padding:2em;font-family:monospace;color:#f88;background:#1e1e1e;min-height:100vh;box-sizing:border-box"
		);
		builder.OpenElement(2, "h3");
		builder.AddContent(3, "Unhandled exception (hot reload: fix the code and save to try again)");
		builder.CloseElement();
		builder.OpenElement(4, "button");
		builder.AddAttribute(5, "style", "font:inherit;cursor:pointer;padding:4px 10px");
		builder.AddAttribute(6, "title", FreshStartTitle);
		builder.AddAttribute(7, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, Runtime.RequestFreshStartAsync));
		builder.AddContent(8, "⟳ Fresh start");
		builder.CloseElement();
		builder.OpenElement(9, "pre");
		builder.AddAttribute(10, "style", "white-space:pre-wrap");
		builder.AddContent(11, exception.ToString());
		builder.CloseElement();
		builder.CloseElement();
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
		builder.AddComponentParameter(
			2,
			nameof(ErrorBoundary.ErrorContent),
			(RenderFragment<Exception>)(ex => b => BuildErrorContent(b, ex))
		);
		builder.CloseComponent();

		if (!_ownsOverlay)
		{
			return;
		}

		var status = Runtime.Status;
		if (status is not null && !ReferenceEquals(status, _dismissedStatus))
		{
			BuildOverlay(builder, status);
		}

		BuildFreshStartButton(builder);
	}

	/// <summary>
	/// A small, always available button for when the page got into a weird state (see <see cref="HotReloadRuntime.RequestFreshStartAsync"/>).
	/// </summary>
	private void BuildFreshStartButton(RenderTreeBuilder builder)
	{
		builder.OpenElement(30, "style");
		builder.AddContent(31, ".dtr-hotreload-fresh{opacity:.35}.dtr-hotreload-fresh:hover{opacity:1}");
		builder.CloseElement();

		builder.OpenElement(32, "div");
		builder.AddAttribute(33, "class", "dtr-hotreload-fresh");
		builder.AddAttribute(
			34,
			"style",
			"position:fixed;left:12px;bottom:12px;z-index:2147483647;cursor:pointer;user-select:none;"
				+ "font:14px/1 monospace;padding:6px 8px;border-radius:12px;background:#1e1e1e;color:#eee;box-shadow:0 2px 8px rgba(0,0,0,.4)"
		);
		builder.AddAttribute(35, "title", FreshStartTitle);
		builder.AddAttribute(36, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, Runtime.RequestFreshStartAsync));
		builder.AddContent(37, "⟳");
		builder.CloseElement();
	}

	/// <summary>
	/// The status toast in the bottom-right corner: green for applied, blue for compiling, red for errors.
	/// </summary>
	private void BuildOverlay(RenderTreeBuilder builder, HotReloadStatus status)
	{
		var accent = status.Kind switch
		{
			HotReloadStatusKind.Applied => "#4caf50",
			HotReloadStatusKind.Compiling => "#2196f3",
			_ => "#f44336",
		};

		builder.OpenElement(10, "div");
		builder.AddAttribute(
			11,
			"style",
			"position:fixed;right:12px;bottom:12px;z-index:2147483647;max-width:min(1000px,90vw);max-height:60vh;overflow:auto;"
				+ "font:12px/1.4 monospace;padding:8px 12px;border-radius:6px;background:#1e1e1e;color:#eee;"
				+ $"border-left:4px solid {accent};box-shadow:0 2px 12px rgba(0,0,0,.5)"
		);

		builder.OpenElement(12, "div");
		builder.AddAttribute(13, "style", "display:flex;gap:12px;align-items:baseline;justify-content:space-between");
		builder.OpenElement(14, "strong");
		builder.AddContent(15, status.Message);
		builder.CloseElement();
		builder.OpenElement(16, "span");
		builder.AddAttribute(17, "style", "cursor:pointer;opacity:.7;white-space:nowrap;margin-left:auto");
		builder.AddAttribute(18, "title", FreshStartTitle);
		builder.AddAttribute(19, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, Runtime.RequestFreshStartAsync));
		builder.AddContent(20, "⟳ fresh start");
		builder.CloseElement();
		builder.OpenElement(21, "span");
		builder.AddAttribute(22, "style", "cursor:pointer;opacity:.7");
		builder.AddAttribute(23, "title", "Dismiss");
		builder.AddAttribute(24, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Dismiss(status)));
		builder.AddContent(25, "✕");
		builder.CloseElement();
		builder.CloseElement();

		if (status.Details.Count > 0)
		{
			builder.OpenElement(26, "pre");
			builder.AddAttribute(27, "style", "white-space:pre-wrap;margin:6px 0 0");
			builder.AddContent(28, string.Join('\n', status.Details));
			builder.CloseElement();
		}

		builder.CloseElement();
	}

	/// <summary>
	/// Remember the exact status object that was dismissed, so a new status with the same text is shown again.
	/// </summary>
	private void Dismiss(HotReloadStatus status)
	{
		_dismissedStatus = status;
		Render();
	}

	/// <summary>
	/// Called from the runtime's status event. Successful updates disappear after a few seconds; errors stay until the next status.
	/// </summary>
	private void OnStatusChanged()
	{
		if (!_ownsOverlay)
		{
			return;
		}

		// A new status replaces the previous one; cancel any pending auto-dismiss.
		_hideStatus?.Cancel();
		_hideStatus?.Dispose();
		_hideStatus = null;

		var status = Runtime.Status;
		if (status?.Kind == HotReloadStatusKind.Applied)
		{
			var cts = _hideStatus = new CancellationTokenSource();
			_ = HideLaterAsync(status, cts.Token);
		}

		_ = _renderHandle.Dispatcher.InvokeAsync(Render);
	}

	/// <summary>
	/// Auto-dismiss an "applied" status, unless another status replaced it in the meantime (which cancels <paramref name="token"/>).
	/// </summary>
	private async Task HideLaterAsync(HotReloadStatus status, CancellationToken token)
	{
		try
		{
			await Task.Delay(AppliedStatusDuration, token).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		await _renderHandle.Dispatcher.InvokeAsync(() => Dismiss(status)).ConfigureAwait(true);
	}
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
