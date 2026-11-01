using System.Collections;
using BlazorHMR.FullSwap;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BlazorHMR.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Photino.Blazor;
using Photino.NET;

namespace BlazorHMR.Photino;

public static class HotReloadExtensions
{
	/// <summary>
	/// Enables hot reload for the Photino Blazor app that <paramref name="configureServices"/> belongs to.
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
	/// <param name="builder">The Photino Blazor app builder.</param>
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
	public static PhotinoBlazorAppBuilder UseHotReload(this PhotinoBlazorAppBuilder builder, Action<IServiceCollection> configureServices)
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

		if (method.GetParameters() is not [{ ParameterType: var servicesType }])
		{
			throw new ArgumentException(
				$"The service configuration needs parameter ({nameof(IServiceCollection)}).",
				nameof(configureServices)
			);
		}

		if (servicesType != typeof(IServiceCollection))
		{
			throw new ArgumentException(
				$"The service configuration needs parameter ({nameof(IServiceCollection)}).",
				nameof(configureServices)
			);
		}

		var appAssembly = method.DeclaringType.Assembly;

		// Swap out the root component(s) for a HotReloadRoot, which renders the root component of the current app generation.
		// Photino's root component list has no parameters, so the root type name is baked into a generic wrapper instead.
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

		// Snapshot the host registrations: the runtime forwards them into every generation, so it needs to know them without
		// holding on to the builder.
		var hostDescriptors = builder.Services.ToList();

		// The WebView manager creates the scope that the renderer uses. Hand it a provider that forwards to the current generation,
		// with a real scope of the host provider under it, so page reloads can start over without restarting the process.
		builder.Services.RemoveAll<PhotinoWebViewManager>();
		builder.Services.AddSingleton(sp =>
		{
			runtime.InitializeHost(sp, hostDescriptors);

			return new PhotinoWebViewManager(
				sp.GetRequiredService<PhotinoWindow>(),
				runtime.RootProvider,
				sp.GetRequiredService<Dispatcher>(),
				sp.GetRequiredService<IFileProvider>(),
				sp.GetRequiredService<JSComponentConfigurationStore>(),
				sp.GetRequiredService<IOptions<PhotinoBlazorAppConfiguration>>()
			);
		});

		return builder;
	}

	/// <summary>
	/// Photino's <see cref="RootComponentList"/> can only add, so the private list is replaced in place.
	/// </summary>
	[SuppressMessage(
		"Major Code Smell",
		"S3011:Reflection should not be used to increase accessibility",
		Justification = "Dev-only tooling, Photino has no API to replace root components"
	)]
	private static int ReplaceRootComponents(PhotinoBlazorAppBuilder builder)
	{
		var field =
			typeof(RootComponentList).GetField("components", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(
				"Field 'RootComponentList.components' was not found; Photino.Blazor has changed, Dtr.Blazor.HotReload needs to be updated."
			);

		if (field.GetValue(builder.RootComponents) is not IList components)
		{
			throw new InvalidOperationException(
				"Field 'RootComponentList.components' is not a list; Photino.Blazor has changed, Dtr.Blazor.HotReload needs to be updated."
			);
		}

		var replaced = 0;
		for (var i = 0; i < components.Count; i++)
		{
			if (components[i] is not ValueTuple<Type, string> entry)
			{
				continue;
			}

			components[i] = (typeof(HotReloadRoot<>).MakeGenericType(entry.Item1), entry.Item2);
			replaced++;
		}

		return replaced;
	}
}
