using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR.FullSwap;

/// <summary>
/// Builds a generation: a DI container with the host-tier services forwarded, plus the services that the app registers.
/// <para>
/// The host-tier services are shared by all generations. Singletons and scoped services are resolved from the host tier and
/// registered as instances (so a generation's container never disposes them); transients and open generics are just
/// registered again.
/// </para>
/// </summary>
internal sealed class GenerationFactory(AppEntryPoint entryPoint, HostTier host)
{
	private IReadOnlyList<ServiceDescriptor>? _indexedDescriptors;
	private int[]? _registrationIndices;

	/// <summary>
	/// Builds one generation: a container with the host-tier services forwarded, plus the services that the app registers.
	/// </summary>
	public AppGeneration Build(int number, Assembly assembly, IReadOnlyDictionary<string, Assembly> assemblies)
	{
		var services = new ServiceCollection();

		ForwardHostServices(services);
		InvokeConfigureServices(assembly, services);

		return new AppGeneration(number, assembly, assemblies, services.BuildServiceProvider());
	}

	private void ForwardHostServices(IServiceCollection services)
	{
		var hostServices = host.Services;
		var descriptors = host.Descriptors;
		var indices = GetRegistrationIndices(descriptors);
		var resolved = new Dictionary<(Type, object?), List<object?>>();

		for (var i = 0; i < descriptors.Count; i++)
		{
			var descriptor = descriptors[i];
			var hasInstance = descriptor.IsKeyedService
				? descriptor.KeyedImplementationInstance is not null
				: descriptor.ImplementationInstance is not null;
			if (hasInstance || descriptor.Lifetime == ServiceLifetime.Transient || descriptor.ServiceType.IsGenericTypeDefinition)
			{
				services.Add(descriptor);
				continue;
			}

			// Multiple registrations for the same service type are resolved in one go, and handed out in registration order.
			var key = (descriptor.ServiceType, descriptor.ServiceKey);
			if (!resolved.TryGetValue(key, out var instances))
			{
				try
				{
					var enumerableType = typeof(IEnumerable<>).MakeGenericType(descriptor.ServiceType);
					var all = descriptor.IsKeyedService
						? ((IKeyedServiceProvider)hostServices).GetKeyedService(enumerableType, descriptor.ServiceKey)
						: hostServices.GetService(enumerableType);
					instances = ((IEnumerable)all!).Cast<object?>().ToList();
				}
				catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
				{
					Console.WriteLine(
						$"[hotreload] Could not resolve host service '{descriptor.ServiceType}', it will be re-created per generation: {ex.Message}"
					);
					instances = [];
				}

				resolved[key] = instances;
			}

			// Resolve<IEnumerable<T>>() returns all registrations for T in registration order, so the precomputed index of this
			// descriptor among the registrations for the same service type/key gives the instance that belongs to it.
			var index = indices[i];
			if (index >= instances.Count || instances[index] is not { } instance)
			{
				// Not resolved yet (or a factory threw): let the new container create its own instance.
				services.Add(descriptor);
				continue;
			}

			services.Add(
				descriptor.IsKeyedService
					? new ServiceDescriptor(descriptor.ServiceType, descriptor.ServiceKey, instance)
					: new ServiceDescriptor(descriptor.ServiceType, instance)
			);
		}
	}

	/// <summary>
	/// The index of every descriptor among the registrations for its own service type/key. Computed once per descriptor list:
	/// doing it per descriptor makes forwarding quadratic in the number of host registrations.
	/// </summary>
	private int[] GetRegistrationIndices(IReadOnlyList<ServiceDescriptor> descriptors)
	{
		if (ReferenceEquals(descriptors, _indexedDescriptors) && _registrationIndices is not null)
		{
			return _registrationIndices;
		}

		var counts = new Dictionary<(Type, object?), int>();
		var indices = new int[descriptors.Count];
		for (var i = 0; i < descriptors.Count; i++)
		{
			var descriptor = descriptors[i];
			var key = (descriptor.ServiceType, descriptor.ServiceKey);
			var index = counts.TryGetValue(key, out var count) ? count : 0;
			indices[i] = index;
			counts[key] = index + 1;
		}

		_indexedDescriptors = descriptors;
		_registrationIndices = indices;
		return indices;
	}

	/// <summary>
	/// Finds the service configuration method in this generation's app assembly (by the name and signature captured at startup)
	/// and runs it against the new container.
	/// </summary>
	private void InvokeConfigureServices(Assembly assembly, IServiceCollection services)
	{
		var type =
			assembly.GetType(entryPoint.StartupTypeName, throwOnError: false)
			?? throw new InvalidOperationException($"Type '{entryPoint.StartupTypeName}' was not found in the app assembly.");

		var parameterTypes = new[] { typeof(IServiceCollection) };
		var method =
			type.GetMethod(
				entryPoint.RegisterServicesMethodName,
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				parameterTypes
			)
			?? throw new InvalidOperationException(
				$"Method '{entryPoint.StartupTypeName}.{entryPoint.RegisterServicesMethodName}' (with the original signature) was not found in the app assembly."
			);

		Invoke(method, [services]);
	}

	/// <summary>
	/// Invokes a static method, unwrapping <see cref="TargetInvocationException"/> so callers see the app's own exception.
	/// </summary>
	private static void Invoke(MethodInfo method, object?[] args)
	{
		try
		{
			method.Invoke(null, args);
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
		}
	}
}
