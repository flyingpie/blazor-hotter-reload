using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorHMR.Blazor;

/// <summary>
/// The overlay that <see cref="HotReloadRoot"/> renders on top of the app: the status toast and the always-available
/// fresh-start button. The error boundary uses the same component (with <see cref="Exception"/> set) for its error panel.
/// Kept out of <see cref="HotReloadRoot"/>, so that component only deals with swapping the generation.
/// </summary>
public sealed class HotReloadOverlay : ComponentBase, IDisposable
{
	private const string FreshStartTitle = "Hot reload: fresh start (recompile everything and reload the page)";

	private static readonly TimeSpan AppliedStatusDuration = TimeSpan.FromSeconds(3);

	private HotReloadStatus? _dismissedStatus;
	private CancellationTokenSource? _hideStatus;
	private bool _disposed;

	/// <summary>
	/// The exception the error boundary caught. When set, the overlay renders the error panel instead of the status toast.
	/// </summary>
	[Parameter]
	public Exception? Exception { get; set; }

	[Inject]
	private HotReloadRuntime Runtime { get; set; } = null!;

	protected override void OnInitialized()
	{
		// The error panel lives inside the error boundary and doesn't need status updates.
		if (Exception is null)
		{
			Runtime.StatusChanged += OnStatusChanged;
		}
	}

	protected override void OnParametersSet()
	{
		// The error boundary may render the same exception more than once; the runtime ignores repeats for one generation.
		if (Exception is { } exception)
		{
			Runtime.OnUnhandledException(exception);
		}
	}

	public void Dispose()
	{
		_disposed = true;
		Runtime.StatusChanged -= OnStatusChanged;
		_hideStatus?.Cancel();
		_hideStatus?.Dispose();
		GC.SuppressFinalize(this);
	}

	protected override void BuildRenderTree(RenderTreeBuilder builder)
	{
		if (Exception is { } exception)
		{
			BuildError(builder, exception);
			BuildFreshStartButton(builder);
			return;
		}

		var status = Runtime.Status;
		if (status is not null && !ReferenceEquals(status, _dismissedStatus))
		{
			BuildStatus(builder, status);
		}

		BuildFreshStartButton(builder);
	}

	/// <summary>
	/// Replaces the app with a readable error and a way out. The runtime uses the exception to detect stale code after an
	/// in-place update and to request a full swap.
	/// </summary>
	private void BuildError(RenderTreeBuilder builder, Exception exception)
	{
		builder.OpenElement(0, "div");
		builder.AddAttribute(1, "role", "alert");
		builder.AddAttribute(
			2,
			"style",
			"padding:2em;font-family:monospace;color:#f88;background:#1e1e1e;min-height:100vh;box-sizing:border-box"
		);
		builder.OpenElement(10, "h3");
		builder.AddContent(11, "Unhandled exception (hot reload: fix the code and save to try again)");
		builder.CloseElement();
		builder.OpenElement(12, "button");
		builder.AddAttribute(13, "style", "font:inherit;cursor:pointer;padding:4px 10px");
		builder.AddAttribute(14, "title", FreshStartTitle);
		builder.AddAttribute(15, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, Runtime.RequestFreshStartAsync));
		builder.AddContent(16, "⟳ Fresh start");
		builder.CloseElement();
		builder.OpenElement(17, "pre");
		builder.AddAttribute(18, "style", "white-space:pre-wrap");
		builder.AddContent(19, exception.ToString());
		builder.CloseElement();
		builder.CloseElement();
	}

	/// <summary>
	/// The status toast in the bottom-right corner: green for applied, blue for compiling, red for errors. It is announced
	/// to assistive technology as a polite status update.
	/// </summary>
	private void BuildStatus(RenderTreeBuilder builder, HotReloadStatus status)
	{
		var accent = status.Kind switch
		{
			HotReloadStatusKind.Applied => "#4caf50",
			HotReloadStatusKind.Compiling => "#2196f3",
			_ => "#f44336",
		};

		builder.OpenElement(0, "div");
		builder.AddAttribute(1, "role", "status");
		builder.AddAttribute(2, "aria-live", "polite");
		builder.AddAttribute(
			3,
			"style",
			"position:fixed;right:12px;bottom:12px;z-index:2147483647;max-width:min(1000px,90vw);max-height:60vh;overflow:auto;"
				+ "font:12px/1.4 monospace;padding:8px 12px;border-radius:6px;background:#1e1e1e;color:#eee;"
				+ $"border-left:4px solid {accent};box-shadow:0 2px 12px rgba(0,0,0,.5)"
		);

		builder.OpenElement(10, "div");
		builder.AddAttribute(11, "style", "display:flex;gap:12px;align-items:baseline;justify-content:space-between");
		builder.OpenElement(12, "strong");
		builder.AddContent(13, status.Message);
		builder.CloseElement();
		builder.OpenElement(14, "span");
		builder.AddAttribute(15, "style", "cursor:pointer;opacity:.7;white-space:nowrap;margin-left:auto");
		builder.AddAttribute(16, "title", FreshStartTitle);
		builder.AddAttribute(17, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, Runtime.RequestFreshStartAsync));
		builder.AddContent(18, "⟳ fresh start");
		builder.CloseElement();
		builder.OpenElement(19, "span");
		builder.AddAttribute(20, "style", "cursor:pointer;opacity:.7");
		builder.AddAttribute(21, "title", "Dismiss");
		builder.AddAttribute(22, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Dismiss(status)));
		builder.AddContent(23, "✕");
		builder.CloseElement();
		builder.CloseElement();

		if (status.Details.Count > 0)
		{
			builder.OpenElement(24, "pre");
			builder.AddAttribute(25, "style", "white-space:pre-wrap;margin:6px 0 0");
			builder.AddContent(26, string.Join('\n', status.Details));
			builder.CloseElement();
		}

		builder.CloseElement();
	}

	/// <summary>
	/// A small, always available button for when the page got into a weird state (see <see cref="HotReloadRuntime.RequestFreshStartAsync"/>).
	/// </summary>
	private void BuildFreshStartButton(RenderTreeBuilder builder)
	{
		builder.OpenElement(30, "style");
		builder.AddContent(31, ".blazor-hmr-fresh{opacity:.35}.blazor-hmr-fresh:hover{opacity:1}");
		builder.CloseElement();

		builder.OpenElement(32, "div");
		builder.AddAttribute(33, "class", "blazor-hmr-fresh");
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
	/// Remember the exact status object that was dismissed, so a new status with the same text is shown again.
	/// </summary>
	private void Dismiss(HotReloadStatus status)
	{
		_dismissedStatus = status;
		StateHasChanged();
	}

	/// <summary>
	/// Called from the runtime's status event. Successful updates disappear after a few seconds; errors stay until the next
	/// status.
	/// </summary>
	private void OnStatusChanged()
	{
		if (_disposed)
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

		_ = InvokeAsync(StateHasChanged);
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

		if (_disposed)
		{
			return;
		}

		await InvokeAsync(() => Dismiss(status)).ConfigureAwait(true);
	}
}
