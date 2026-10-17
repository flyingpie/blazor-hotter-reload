using Microsoft.Extensions.DependencyInjection;

namespace Dtr.Blazor.HotReload;

/// <summary>
/// Hands Blazor a <see cref="SwappableServiceProvider"/> instead of a regular container, so the services can be replaced on every hot reload.
/// </summary>
internal sealed class HotReloadServiceProviderFactory(HotReloadRuntime runtime) : IServiceProviderFactory<IServiceCollection>
{
	public IServiceCollection CreateBuilder(IServiceCollection services) => services;

	public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder) => runtime.Initialize(containerBuilder);
}
