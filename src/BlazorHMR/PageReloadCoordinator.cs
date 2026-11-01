using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorHMR;

/// <summary>
/// Everything that touches the page itself: the JS module, reloading stylesheets, and a forced page reload. Separate from the
/// server connection, because a desktop page reload keeps the process (and the connection) alive but replaces all of this.
/// </summary>
internal sealed class PageReloadCoordinator(IServiceProvider hostServices)
{
	private IJSObjectReference? _module;

	/// <summary>
	/// The services of the current page.
	/// </summary>
	public IServiceProvider HostServices { get; private set; } = hostServices;

	/// <summary>
	/// A desktop page reload keeps the process alive, but the page's services and JS runtime are new.
	/// </summary>
	public void Reattach(IServiceProvider hostServices)
	{
		HostServices = hostServices;

		// The JS module was imported into the old page's JS runtime, so it has to be imported again on the next call.
		_module = null;
	}

	/// <summary>
	/// Reloads the page at most once per <paramref name="key"/> in this tab, so a page that can't apply patches doesn't reload
	/// in a loop. Returns false when it was already reloaded for this key.
	/// </summary>
	public async Task<bool> ReloadOnceAsync(string key)
	{
		var module = await GetModuleAsync().ConfigureAwait(true);
		return await module.InvokeAsync<bool>("reloadOnce", key).ConfigureAwait(true);
	}

	/// <summary>
	/// CSS isn't compiled into an assembly, so reload the matching stylesheet(s) by cache-busting their URLs.
	/// </summary>
	public async Task ReloadCssAsync(string path)
	{
		try
		{
			var module = await GetModuleAsync().ConfigureAwait(true);
			await module.InvokeVoidAsync("reloadCss", path).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[hotreload] CSS reload failed: {ex.Message}");
		}
	}

	/// <summary>
	/// A forced navigation reload. On the desktop the process stays alive, but the page (and its service scope) starts over.
	/// </summary>
	public Task ReloadPageAsync()
	{
		Console.WriteLine("[hotreload] Reloading page");
		_module = null;
		HostServices.GetRequiredService<NavigationManager>().Refresh(forceReload: true);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Imports the package's JS module once per page (a page reload resets it).
	/// </summary>
	private async Task<IJSObjectReference> GetModuleAsync()
	{
		var js = HostServices.GetRequiredService<IJSRuntime>();
		return _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/BlazorHMR/hotreload.js").ConfigureAwait(true);
	}
}
