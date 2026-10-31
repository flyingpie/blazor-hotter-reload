using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR;

/// <summary>
/// One version of the app: its assemblies, plus the DI container that was built from them.
/// Every full swap creates a new generation, and throws away the old one (after carrying over what state it can).
/// </summary>
public sealed class AppGeneration : IAsyncDisposable
{
	private readonly ServiceProvider _provider;
	private readonly AsyncServiceScope _scope;

	public AppGeneration(int number, Assembly assembly, IReadOnlyDictionary<string, Assembly> assemblies, ServiceProvider provider)
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
	/// The DI container's root and the scope, i.e. what holds the created singletons and scoped services.
	/// </summary>
	[SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility", Justification = "DI internals")]
	public IEnumerable<object> EngineScopes =>
		new[]
		{
			typeof(ServiceProvider).GetProperty("Root", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_provider),
			_scope.ServiceProvider,
		}.OfType<object>();

	/// <summary>
	/// The version of an assembly that this generation uses: its own, or the one the page started with.
	/// </summary>
	public Assembly? GetAssembly(string name) =>
		Assemblies.TryGetValue(name, out var assembly)
			? assembly
			: AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name);

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
