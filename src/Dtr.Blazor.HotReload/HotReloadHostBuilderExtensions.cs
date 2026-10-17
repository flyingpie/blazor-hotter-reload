using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dtr.Blazor.HotReload;

public static class HotReloadHostBuilderExtensions
{
	/// <summary>
	/// Enables hot reload for the app that <paramref name="configureServices"/> belongs to.
	/// <para>
	/// Call this <i>instead of</i> registering the app services directly, after the root components have been added.
	/// The services registered on the builder at this point (framework defaults, logging) end up in the "host" tier, which lives as long as the page does.
	/// The services registered by <paramref name="configureServices"/> are (re-)created for every new version of the app assembly.
	/// </para>
	/// </summary>
	/// <param name="builder">The host builder.</param>
	/// <param name="configureServices">
	/// A static method in the app assembly that registers the app services.
	/// On every hot reload, the method with the same name on the type with the same name is looked up in the new app assembly, and invoked.
	/// </param>
	public static WebAssemblyHostBuilder UseHotReload(
		this WebAssemblyHostBuilder builder,
		Action<IServiceCollection, IConfiguration, IWebAssemblyHostEnvironment> configureServices
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

		var appAssembly = method.DeclaringType.Assembly;

		// Swap out the app's root component(s) for a HotReloadRoot, which renders the root component of the current app generation.
		// Only roots from the app assembly are replaced: framework/package roots aren't hot reloaded.
		var rootTypeNames = new List<string>();
		for (var i = 0; i < builder.RootComponents.Count; i++)
		{
			var mapping = builder.RootComponents[i];
			if (mapping.ComponentType.Assembly != appAssembly)
			{
				continue;
			}

			rootTypeNames.Add(mapping.ComponentType.FullName!);
			builder.RootComponents[i] = new RootComponentMapping(
				typeof(HotReloadRoot),
				mapping.Selector,
				ParameterView.FromDictionary(
					new Dictionary<string, object?>(StringComparer.Ordinal)
					{
						[nameof(HotReloadRoot.RootTypeName)] = mapping.ComponentType.FullName,
					}
				)
			);
		}

		if (rootTypeNames.Count == 0)
		{
			throw new InvalidOperationException(
				$"No root components from assembly '{appAssembly.GetName().Name}' were found, call UseHotReload() after adding the root components."
			);
		}

		var runtime = new HotReloadRuntime(
			new AppEntryPoint(method.DeclaringType.FullName!, method.Name),
			appAssembly,
			builder.Configuration,
			builder.HostEnvironment,
			HotReloadOptions.FromConfiguration(builder.Configuration)
		);

		// Available from both the host tier and every app generation (instances are forwarded as-is).
		builder.Services.AddSingleton(runtime);

		// Let the host builder create the root provider through the runtime, so it can swap the app's services later.
		builder.ConfigureContainer(new HotReloadServiceProviderFactory(runtime));

		return builder;
	}
}
