using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR;

/// <summary>
/// Hands Blazor a <see cref="SwappableServiceProvider"/> instead of a regular container, so the services can be replaced on every hot reload.
/// </summary>
public sealed class HotReloadServiceProviderFactory(HotReloadRuntime runtime, bool desktop = false)
	: IServiceProviderFactory<IServiceCollection>
{
	/// <summary>
	/// The host builder registers its services in the collection it gets here; the runtime needs them to forward the host tier.
	/// </summary>
	public IServiceCollection CreateBuilder(IServiceCollection services) => services;

	/// <summary>
	/// Called once by the host when it builds its container. Browser and desktop hosts have different ownership of the page
	/// scope, hence the two initialization paths.
	/// </summary>
	public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder) =>
		desktop ? runtime.InitializeDesktop(containerBuilder) : runtime.Initialize(containerBuilder);
}
