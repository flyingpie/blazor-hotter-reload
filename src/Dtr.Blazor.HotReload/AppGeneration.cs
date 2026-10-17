using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Dtr.Blazor.HotReload;

/// <summary>
/// One version of the app: its assemblies, plus the DI container that was built from them.
/// Every full swap creates a new generation, and throws away the old one.
/// </summary>
public sealed class AppGeneration : IAsyncDisposable
{
	private readonly ServiceProvider _provider;
	private readonly AsyncServiceScope _scope;

	internal AppGeneration(int number, Assembly assembly, IReadOnlyDictionary<string, Assembly> assemblies, ServiceProvider provider)
	{
		Number = number;
		Assembly = assembly;
		Assemblies = assemblies;
		_provider = provider;
		_scope = provider.CreateAsyncScope();
	}

	/// <summary>
	/// 0 for the assembly the app booted with, incremented on every full swap.
	/// </summary>
	public int Number { get; }

	/// <summary>
	/// The app assembly.
	/// </summary>
	public Assembly Assembly { get; }

	/// <summary>
	/// The assemblies that are specific to this generation (the app, plus the libraries that were swapped), by name.
	/// </summary>
	public IReadOnlyDictionary<string, Assembly> Assemblies { get; }

	/// <summary>
	/// Blazor WebAssembly only uses a single scope, this is it.
	/// </summary>
	public IServiceProvider Services => _scope.ServiceProvider;

	/// <summary>
	/// Disposes the scope first, then the root container. Both only contain this generation's services; forwarded host-tier
	/// instances are registered as instances, so the host container still owns (and disposes) them.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		await _scope.DisposeAsync().ConfigureAwait(false);
		await _provider.DisposeAsync().ConfigureAwait(false);
	}
}
