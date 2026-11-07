using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR.Bcl;

/// <summary>
/// Factory for the custom <see cref="SwappableServiceProvider"/>.
/// </summary>
public sealed class HotReloadServiceProviderFactory(HotReloadRuntime runtime) : IServiceProviderFactory<IServiceCollection>
{
	/// <inheritdoc/>
	public IServiceCollection CreateBuilder(IServiceCollection services) => services;

	/// <inheritdoc/>
	public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder) => runtime.InitializeDesktop(containerBuilder);
}
