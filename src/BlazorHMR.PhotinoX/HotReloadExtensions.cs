using BlazorHMR.Bcl;
using BlazorHMR.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace BlazorHMR.PhotinoX;

public static class HotReloadExtensions
{
	/// <summary>
	/// Enables hot reload for the PhotinoX Blazor app that <paramref name="configureServices"/> belongs to.
	/// <para>
	/// Call this <i>instead of</i> registering the app services directly, after the root components have been added, and before
	/// <c>Build()</c>. Services registered on the builder before this point (framework defaults, logging, Photino) end up in the
	/// "host" tier, which lives as long as the process does. The services registered by <paramref name="configureServices"/> are
	/// (re-)created for every new version of the app assembly.
	/// </para>
	/// <para>
	/// Unlike the WebAssembly host, a page reload doesn't restart the process: the generation is rebuilt on top of the new page's
	/// service scope, and the loaded assemblies (including in-place updates) are kept.
	/// </para>
	/// </summary>
	/// <param name="builder">The PhotinoX Blazor app builder.</param>
	/// <param name="configuration">The app configuration, passed to <paramref name="configureServices"/> and used for the hot reload options.</param>
	/// <param name="configureServices">
	/// A static method in the app assembly that registers the app services.
	/// On every hot reload, the method with the same name on the type with the same name is looked up in the new app assembly, and invoked.
	/// It must have the signature <c>(IServiceCollection, IConfiguration)</c>.
	/// </param>
	/// <param name="initializeServices">
	/// Optional static method (on the same type as <paramref name="configureServices"/>) that initializes static state, once the services
	/// have been built, e.g. <c>Log.Setup(...)</c>. Hot reloaded libraries start out with fresh static state, so this is called for every new
	/// generation, and whenever the page (re)attaches. The app itself only needs to call it for non-hot-reload builds.
	/// </param>
	public static PhotinoBlazorAppBuilder UseHotReload(
		this PhotinoBlazorAppBuilder builder,
		Action<IServiceCollection> configureServices,
		Action<IServiceProvider>? initializeServices = null
	)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configureServices);

		// Capture the app assembly and the method identity now: every new generation looks up the same method there.
		var method = configureServices.Method;
		if (!method.IsStatic || method.DeclaringType is null)
		{
			throw new ArgumentException(
				"The service configuration needs to be a static method, so it can be found in new versions of the app assembly.",
				nameof(configureServices)
			);
		}

		if (method.GetParameters() is not [{ ParameterType: var servicesType }])
		{
			throw new ArgumentException(
				$"The service configuration needs parameters ({nameof(IServiceCollection)}).",
				nameof(configureServices)
			);
		}

		if (servicesType != typeof(IServiceCollection))
		{
			throw new ArgumentException(
				$"The service configuration needs two parameters ({nameof(IServiceCollection)}, {nameof(IConfiguration)}).",
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

		// Swap out the root component(s) for a HotReloadRoot, which renders the root component of the current app generation.
		// PhotinoX's root components are a collection, so the entries are replaced in place.
		var roots = ReplaceRootComponents(builder);
		if (roots == 0)
		{
			throw new InvalidOperationException("No root components were found, call UseHotReload() after adding the root components.");
		}

		var runtime = new HotReloadRuntime(new AppEntryPoint(method.DeclaringType.FullName!, method.Name), appAssembly);

		// Available from both the host tier and every app generation (instances are forwarded as-is).
		builder.Services.AddSingleton(runtime);

		// Lets new components pick up the state of their predecessors after a full swap.
		builder.Services.AddSingleton<IComponentActivator>(new HotReloadComponentActivator(runtime));

		// PhotinoX builds the host provider from the builder services; the runtime hands Blazor a provider that forwards to the
		// current generation. The host owns the page scope, so page reloads start over without restarting the process.
		builder.ConfigureContainer(new HotReloadServiceProviderFactory(runtime));

		return builder;
	}

	/// <summary>
	/// Replaces every root component with a <see cref="HotReloadRoot{T}"/> for its type. The generic keeps the root type
	/// name without needing root component parameters (which PhotinoX can pass, but the hot reload root doesn't preserve).
	/// </summary>
	private static int ReplaceRootComponents(PhotinoBlazorAppBuilder builder)
	{
		var replaced = 0;
		for (var i = 0; i < builder.RootComponents.Count; i++)
		{
			var root = builder.RootComponents[i];
			builder.RootComponents[i] = new RootComponent
			{
				ComponentType = typeof(HotReloadRoot<>).MakeGenericType(root.ComponentType),
				Selector = root.Selector,
			};
			replaced++;
		}

		return replaced;
	}
}
