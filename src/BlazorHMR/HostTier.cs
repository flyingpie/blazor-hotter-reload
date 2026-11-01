using BlazorHMR.FullSwap;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR;

/// <summary>
/// The host tier: the services that live as long as the process does, and the page scope that the generations are built on.
/// Browser hosts build one scope; desktop hosts get a new one when the page (re)attaches, so the app can start over without
/// restarting the process.
/// </summary>
internal sealed class HostTier : IAsyncDisposable
{
	private IReadOnlyList<ServiceDescriptor> _descriptors = [];
	private IServiceProvider? _hostRoot;
	private ServiceProvider? _hostProvider;
	private AsyncServiceScope? _hostScope;

	/// <summary>
	/// The host registrations, forwarded into every generation.
	/// </summary>
	public IReadOnlyList<ServiceDescriptor> Descriptors => _descriptors;

	/// <summary>
	/// The services of the current page scope.
	/// </summary>
	public IServiceProvider Services =>
		(_hostScope ?? throw new InvalidOperationException("Hot reload has not been initialized yet.")).ServiceProvider;

	/// <summary>
	/// Called by the container factory of a host that builds the host provider itself (PhotinoX): the host owns the page scope.
	/// </summary>
	public void InitializeDesktop(IServiceCollection hostServices)
	{
		_descriptors = [.. hostServices];
		_hostProvider = hostServices.BuildServiceProvider();
		_hostRoot = _hostProvider;
		_hostScope = _hostProvider.CreateAsyncScope();
	}

	/// <summary>
	/// Called by a desktop host that already has its provider (Photino): the page scope is created when the page attaches
	/// (see <see cref="ReplaceScope"/>).
	/// </summary>
	public void InitializeHost(IServiceProvider hostRoot, IReadOnlyList<ServiceDescriptor> hostDescriptors)
	{
		_hostRoot = hostRoot;
		_descriptors = hostDescriptors;
	}

	/// <summary>
	/// Creates the scope for a page that attaches (or reloads) and makes it current. Returns the previous scope, which belongs
	/// to the generation that is being replaced; the caller disposes it once that generation is gone.
	/// </summary>
	public AsyncServiceScope? ReplaceScope()
	{
		var hostRoot = _hostRoot ?? throw new InvalidOperationException("The host service provider has not been set.");
		var scope = hostRoot.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
		var previous = _hostScope;
		_hostScope = scope;
		return previous;
	}

	/// <summary>
	/// Undoes <see cref="ReplaceScope"/> when building the new generation failed: the new scope is dropped and the previous one
	/// becomes current again.
	/// </summary>
	public void RestoreScope(AsyncServiceScope? previous)
	{
		_hostScope?.Dispose();
		_hostScope = previous;
	}

	public async ValueTask DisposeAsync()
	{
		if (_hostScope is { } hostScope)
		{
			await hostScope.DisposeAsync().ConfigureAwait(false);
		}

		if (_hostProvider is not null)
		{
			await _hostProvider.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Disposes the generation of a page that went away, and the host scope it was built on. Failures are logged: a page reload
	/// must not be taken down by a misbehaving service.
	/// </summary>
	public static async Task DisposeGenerationAsync(AppGeneration generation, AsyncServiceScope? scope)
	{
		try
		{
			await generation.DisposeAsync().ConfigureAwait(false);
			if (scope is { } hostScope)
			{
				await hostScope.DisposeAsync().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[hotreload] Could not dispose the previous page: {ex}");
		}
	}
}
