using BlazorHMR.Bcl;
using BlazorHMR.Blazor;
using BlazorHMR.FullSwap;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR.WebAssembly;

public static class HotReloadExtensions
{
	/// <summary>
	/// Enables hot reload for the Blazor WebAssembly app that <paramref name="configureServices"/> belongs to.
	/// <para>
	/// Call this <i>instead of</i> registering the app services directly, after the root components have been added, and before
	/// <c>Build()</c>. Services registered on the builder (framework defaults, logging, configuration, host environment) end up
	/// in the "host" tier, which lives as long as the page does. The services registered by <paramref name="configureServices"/>
	/// are (re-)created for every new version of the app assembly.
	/// </para>
	/// <para>
	/// A page reload starts the .NET runtime over, so it always runs the latest build output; the server then sends the latest
	/// generation and patches on top of it.
	/// </para>
	/// </summary>
	/// <param name="builder">The WebAssembly host builder.</param>
	/// <param name="configureServices">
	/// A static method in the app assembly that registers the app services, with the signature <c>(IServiceCollection)</c>.
	/// On every hot reload, the method with the same name on the type with the same name is looked up in the new app assembly,
	/// and invoked. The builder's <c>IConfiguration</c> and <c>IWebAssemblyHostEnvironment</c> are host services, so the app can
	/// resolve them from its registrations (e.g. <c>sp.GetRequiredService&lt;IWebAssemblyHostEnvironment&gt;().BaseAddress</c>).
	/// </param>
	/// <param name="initializeServices">
	/// Optional static method (on the same type as <paramref name="configureServices"/>) that initializes static state, once the
	/// services have been built, e.g. <c>Log.Setup(...)</c>. Hot reloaded libraries start out with fresh static state, so this is
	/// called for every new generation, including the first. The app itself only needs to call it for non-hot-reload builds.
	/// </param>
	public static WebAssemblyHostBuilder UseHotReload(
		this WebAssemblyHostBuilder builder,
		Action<IServiceCollection> configureServices,
		Action<IServiceProvider>? initializeServices = null
	)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configureServices);

		// Capture the app assembly and the method identity now: every new generation looks up the same method there.
		// A delegate gives us the method, not just the services; the app's ConfigureServices is static for this reason.
		var method = configureServices.Method;
		if (!method.IsStatic || method.DeclaringType is null)
		{
			throw new ArgumentException(
				"The service configuration needs to be a static method, so it can be found in new versions of the app assembly.",
				nameof(configureServices)
			);
		}

		if (
			initializeServices is not null
			&& (!initializeServices.Method.IsStatic || initializeServices.Method.DeclaringType != method.DeclaringType)
		)
		{
			throw new ArgumentException(
				$"The initialization needs to be a static method on '{method.DeclaringType}', so it can be found in new versions of the app assembly.",
				nameof(initializeServices)
			);
		}

		var appAssembly = method.DeclaringType.Assembly;

		// Swap out the app's root component(s) for a HotReloadRoot, which renders the root component of the current app generation.
		// Only roots from the app assembly are replaced: framework roots (e.g. HeadOutlet) aren't hot reloaded.
		var roots = 0;
		for (var i = 0; i < builder.RootComponents.Count; i++)
		{
			var mapping = builder.RootComponents[i];
			if (mapping.ComponentType.Assembly != appAssembly)
			{
				continue;
			}

			builder.RootComponents[i] = new RootComponentMapping(
				typeof(HotReloadRoot<>).MakeGenericType(mapping.ComponentType),
				mapping.Selector
			);
			roots++;
		}

		if (roots == 0)
		{
			throw new InvalidOperationException(
				$"No root components from assembly '{appAssembly.GetName().Name}' were found, call UseHotReload() after adding the root components."
			);
		}

		var runtime = new HotReloadRuntime(
			new AppEntryPoint(method.DeclaringType.FullName!, method.Name, initializeServices?.Method.Name),
			appAssembly
		);

		// Available from both the host tier and every app generation (instances are forwarded as-is).
		builder.Services.AddSingleton(runtime);

		// Lets new components pick up the state of their predecessors after a full swap.
		builder.Services.AddSingleton<IComponentActivator>(new HotReloadComponentActivator(runtime));

		// Let the host builder create the root provider through the runtime, so it can swap the app's services later.
		builder.ConfigureContainer(new HotReloadServiceProviderFactory(runtime));

		return builder;
	}
}
