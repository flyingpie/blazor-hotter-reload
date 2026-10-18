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
public sealed class HotReloadRoot : IComponent, IDisposable
{
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

	public void Attach(RenderHandle renderHandle) => _renderHandle = renderHandle;

	public Task SetParametersAsync(ParameterView parameters)
	{
		parameters.SetParameterProperties(this);

		if (!_initialized)
		{
			_initialized = true;
			// With multiple app roots, only the first one shows the overlay.
			_ownsOverlay = !Runtime.HasOverlayOwner;
			Runtime.HasOverlayOwner = true;
			Runtime.StatusChanged += OnStatusChanged;
			Runtime.Attach(this, GetRenderer(_renderHandle));
		}

		Render();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		Runtime.StatusChanged -= OnStatusChanged;
		Runtime.Detach(this);
		_hideStatus?.Cancel();
		_hideStatus?.Dispose();
	}

	internal Task RenderGenerationAsync() => _renderHandle.Dispatcher.InvokeAsync(Render);

	private static Renderer GetRenderer(RenderHandle handle)
	{
		var field =
			typeof(RenderHandle).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(
				"Field 'RenderHandle._renderer' was not found; Blazor internals have changed, Dtr.Blazor.HotReload needs to be updated."
			);

		return (Renderer)field.GetValue(handle)!;
	}

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
		builder.OpenElement(4, "pre");
		builder.AddAttribute(5, "style", "white-space:pre-wrap");
		builder.AddContent(6, exception.ToString());
		builder.CloseElement();
		builder.CloseElement();
	}

	private void Render() => _renderHandle.Render(BuildRenderTree);

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

		var status = Runtime.Status;
		if (_ownsOverlay && status is not null && !ReferenceEquals(status, _dismissedStatus))
		{
			BuildOverlay(builder, status);
		}
	}

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
		builder.AddAttribute(17, "style", "cursor:pointer;opacity:.7");
		builder.AddAttribute(18, "title", "Dismiss");
		builder.AddAttribute(19, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Dismiss(status)));
		builder.AddContent(20, "✕");
		builder.CloseElement();
		builder.CloseElement();

		if (status.Details.Count > 0)
		{
			builder.OpenElement(21, "pre");
			builder.AddAttribute(22, "style", "white-space:pre-wrap;margin:6px 0 0");
			builder.AddContent(23, string.Join('\n', status.Details));
			builder.CloseElement();
		}

		builder.CloseElement();
	}

	private void Dismiss(HotReloadStatus status)
	{
		_dismissedStatus = status;
		Render();
	}

	private void OnStatusChanged()
	{
		if (!_ownsOverlay)
		{
			return;
		}

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
