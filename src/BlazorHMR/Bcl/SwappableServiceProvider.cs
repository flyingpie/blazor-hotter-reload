using BlazorHMR.FullSwap;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR.Bcl;

/// <summary>
/// The service provider that Blazor (the host and the renderer) sees. Forwards everything to the current <see cref="AppGeneration"/>.
/// <para>
/// There's a root instance (what the host builder gets from the container factory), and a scope instance
/// (what the host creates for the renderer, and what components get their <c>[Inject]</c>ed services from).
/// Scopes created by the app itself (e.g. <c>OwningComponentBase</c>) are real scopes of the current generation.
/// </para>
/// </summary>
public sealed class SwappableServiceProvider(HotReloadRuntime runtime, bool isRoot)
	: IKeyedServiceProvider,
		ISupportRequiredService,
		IServiceScopeFactory,
		IServiceScope,
		IAsyncDisposable
{
	private readonly HotReloadRuntime _runtime = runtime;

	/// <summary>
	/// Whether this is the very first and root-most service provider. The root instance stays alive as long as the process does.<br/>
	/// Child providers are constructed for every app generation, and contain most of the app's actual services.
	/// </summary>
	private readonly bool _isRoot = isRoot;

	/// <summary>Required by IServiceScope.</summary>
	public IServiceProvider ServiceProvider => this;

	private IServiceProvider Current => _runtime.Current.Services;

	public object? GetService(Type serviceType)
	{
		// If asking for the service provider itself.
		if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IKeyedServiceProvider))
		{
			return this;
		}

		// If a new scope is requested, and we are the root, return ourselves.
		if (_isRoot && serviceType == typeof(IServiceScopeFactory))
		{
			return this;
		}

		// Delegate to child provider.
		return Current.GetService(serviceType);
	}

	public object GetRequiredService(Type serviceType) =>
		GetService(serviceType) // Take into account the logic in GetService().
		?? Current.GetRequiredService(serviceType); // Redirect request to the service.

	public object? GetKeyedService(Type serviceType, object? serviceKey) =>
		((IKeyedServiceProvider)Current).GetKeyedService(serviceType, serviceKey);

	public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
		((IKeyedServiceProvider)Current).GetRequiredKeyedService(serviceType, serviceKey);

	/// <summary>
	/// Only reachable through the root instance.<br/>
	/// A new page attach asks for a new scope, which gets a real host scope under it.
	/// </summary>
	public IServiceScope CreateScope()
	{
		if (_isRoot)
		{
			return _runtime.CreateHostScope();
		}

		return new SwappableServiceProvider(_runtime, isRoot: false);
	}

	public void Dispose()
	{
		// Scopes are owned by the generations; nothing to do here.
	}

	public async ValueTask DisposeAsync()
	{
		if (_isRoot)
		{
			await _runtime.DisposeAsync().ConfigureAwait(false);
		}
	}
}
