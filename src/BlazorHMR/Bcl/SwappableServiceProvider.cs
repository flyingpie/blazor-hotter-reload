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
public sealed class SwappableServiceProvider
	: IKeyedServiceProvider,
		ISupportRequiredService,
		IServiceScopeFactory,
		IServiceScope,
		IAsyncDisposable
{
	private readonly HotReloadRuntime _runtime;
	private readonly bool _isRoot;

	public SwappableServiceProvider(HotReloadRuntime runtime, bool isRoot)
	{
		_runtime = runtime;
		_isRoot = isRoot;
	}

	public IServiceProvider ServiceProvider => this;

	private IServiceProvider Current => _runtime.Current.Services;

	public object? GetService(Type serviceType)
	{
		// Anything that asks for the container itself must get this forwarding provider, not the current generation's (which
		// would be stale after a swap).
		if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IKeyedServiceProvider))
		{
			return this;
		}

		// The host asks the root provider for a scope factory, to create the one scope that the renderer uses. On the desktop
		// this is also how a new page gets a new host scope.
		if (_isRoot && serviceType == typeof(IServiceScopeFactory))
		{
			return this;
		}

		return Current.GetService(serviceType);
	}

	public object GetRequiredService(Type serviceType) => GetService(serviceType) ?? Current.GetRequiredService(serviceType);

	public object? GetKeyedService(Type serviceType, object? serviceKey) =>
		((IKeyedServiceProvider)Current).GetKeyedService(serviceType, serviceKey);

	public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
		((IKeyedServiceProvider)Current).GetRequiredKeyedService(serviceType, serviceKey);

	/// <summary>
	/// Only reachable through the root instance. In the browser this creates the (single) renderer scope; on the desktop
	/// a new page attach asks for a new scope, which gets a real host scope under it.
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
