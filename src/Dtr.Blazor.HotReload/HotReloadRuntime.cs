using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dtr.Blazor.HotReload;

/// <summary>
/// Owns the app generations, and swaps them when a new app assembly comes in.
/// <para>
/// The idea is borrowed from s&amp;box's hotload (compile a complete new assembly, load it next to the old one), except that we
/// don't migrate any objects to the new types. Instead, the whole app (DI container + component tree) is rebuilt from the new assembly.
/// That loses in-memory state, but it means that pretty much <i>any</i> change can be applied.
/// </para>
/// </summary>
[SuppressMessage(
	"Major Code Smell",
	"S3011:Reflection should not be used to increase accessibility",
	Justification = "Dev-only tooling, poking Blazor internals is the point"
)]
[SuppressMessage("Usage", "BL0006:Do not use RenderTree types", Justification = "We need the renderer to update its cascading values")]
public sealed class HotReloadRuntime : IAsyncDisposable
{
	private const string CascadingValueSupplierTypeName = "Microsoft.AspNetCore.Components.ICascadingValueSupplier";
	private const string CascadingValueSuppliersFieldName = "<ServiceProviderCascadingValueSuppliers>k__BackingField";

	private readonly AppEntryPoint _entryPoint;
	private readonly Assembly _initialAssembly;
	private readonly IConfiguration _configuration;
	private readonly IWebAssemblyHostEnvironment _environment;
	private readonly SemaphoreSlim _swapLock = new(1, 1);
	private readonly List<HotReloadRoot> _roots = [];

	private IReadOnlyList<ServiceDescriptor> _hostDescriptors = [];
	private ServiceProvider? _hostProvider;
	private AsyncServiceScope _hostScope;
	private AppGeneration? _current;
	private Renderer? _renderer;

	internal HotReloadRuntime(
		AppEntryPoint entryPoint,
		Assembly initialAssembly,
		IConfiguration configuration,
		IWebAssemblyHostEnvironment environment,
		HotReloadOptions options
	)
	{
		_entryPoint = entryPoint;
		_initialAssembly = initialAssembly;
		_configuration = configuration;
		_environment = environment;
		Options = options;
	}

	internal event Action? StatusChanged;

	public HotReloadOptions Options { get; }

	public AppGeneration Current => _current ?? throw new InvalidOperationException("Hot reload has not been initialized yet.");

	/// <summary>
	/// What the overlay shows.
	/// </summary>
	public HotReloadStatus? Status { get; private set; }

	internal bool HasOverlayOwner { get; set; }

	/// <summary>
	/// Loads a new version of the app assembly, and swaps the whole app over to it.
	/// Throws when anything goes wrong before the swap; the current generation stays active in that case.
	/// </summary>
	public async Task ApplyAsync(IReadOnlyList<GenerationAssembly> assemblies)
	{
		ArgumentNullException.ThrowIfNull(assemblies);
		ArgumentOutOfRangeException.ThrowIfZero(assemblies.Count);

		await _swapLock.WaitAsync().ConfigureAwait(true);
		try
		{
			var stopwatch = Stopwatch.StartNew();
			var number = Current.Number + 1;

			var loadContext = new GenerationLoadContext(number, assemblies);
			var newAssembly = loadContext.Get(_initialAssembly.GetName().Name!);

			var next = BuildGeneration(number, newAssembly);
			var previous = Current;

			_current = next;
			UpdateRendererCascadingValueSuppliers(next);

			// Re-render all roots with the root components of the new generation (which disposes the old component tree).
			foreach (var root in _roots.ToList())
			{
				await root.RenderGenerationAsync().ConfigureAwait(true);
			}

			await previous.DisposeAsync().ConfigureAwait(true);

			stopwatch.Stop();
			Console.WriteLine(
				$"[hotreload] Applied generation {number} ({string.Join(", ", assemblies.Select(a => a.Name))}) in {stopwatch.ElapsedMilliseconds} ms"
			);
			SetStatus(
				new HotReloadStatus(
					HotReloadStatusKind.Applied,
					$"Hot reload applied, app restarted (generation {number}, {stopwatch.ElapsedMilliseconds} ms)",
					[]
				)
			);
		}
		finally
		{
			_swapLock.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_current is not null)
		{
			await _current.DisposeAsync().ConfigureAwait(false);
		}

		await _hostScope.DisposeAsync().ConfigureAwait(false);
		if (_hostProvider is not null)
		{
			await _hostProvider.DisposeAsync().ConfigureAwait(false);
		}

		_swapLock.Dispose();
	}

	/// <summary>
	/// Called by the container factory, with the services that were registered on the host builder (i.e. the "host" tier).
	/// </summary>
	internal IServiceProvider Initialize(IServiceCollection hostServices)
	{
		_hostDescriptors = [.. hostServices];
		_hostProvider = hostServices.BuildServiceProvider();
		_hostScope = _hostProvider.CreateAsyncScope();

		// Generation 0 is built the exact same way as all the next ones, so problems with that show up right away.
		_current = BuildGeneration(0, _initialAssembly);

		return new SwappableServiceProvider(this, isRoot: true);
	}

	internal void Attach(HotReloadRoot root, Renderer renderer)
	{
		_roots.Add(root);

		if (_renderer is not null)
		{
			return;
		}

		_renderer = renderer;

		// Make sure that poking the renderer works, before anyone tries to hot reload anything.
		_ = GetCascadingValueSuppliersField();
	}

	internal void Detach(HotReloadRoot root) => _roots.Remove(root);

	internal Type GetRootType(string typeName) =>
		Current.Assembly.GetType(typeName, throwOnError: false)
		?? throw new InvalidOperationException($"Root component '{typeName}' was not found in generation {Current.Number}.");

	internal void SetStatus(HotReloadStatus? status)
	{
		Status = status;
		StatusChanged?.Invoke();
	}

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

	private static FieldInfo GetCascadingValueSuppliersField() =>
		typeof(Renderer).GetField(CascadingValueSuppliersFieldName, BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException(
			$"Field 'Renderer.{CascadingValueSuppliersFieldName}' was not found; Blazor internals have changed, Dtr.Blazor.HotReload needs to be updated."
		);

	private AppGeneration BuildGeneration(int number, Assembly assembly)
	{
		var services = new ServiceCollection();

		ForwardHostServices(services);
		InvokeConfigureServices(assembly, services);

		return new AppGeneration(number, assembly, services.BuildServiceProvider());
	}

	/// <summary>
	/// Host-tier services are shared by all generations. Singletons and scoped services are resolved from the host tier, and registered as
	/// instances (so a generation's container never disposes them). Transients and open generics are just registered again.
	/// </summary>
	private void ForwardHostServices(IServiceCollection services)
	{
		var hostServices = _hostScope.ServiceProvider;
		var resolved = new Dictionary<(Type, object?), List<object?>>();

		foreach (var descriptor in _hostDescriptors)
		{
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
						? hostServices.GetKeyedService(enumerableType, descriptor.ServiceKey)
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

			var index = _hostDescriptors
				.Where(d => d.ServiceType == descriptor.ServiceType && Equals(d.ServiceKey, descriptor.ServiceKey))
				.ToList()
				.IndexOf(descriptor);
			if (index < 0 || index >= instances.Count || instances[index] is not { } instance)
			{
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

	private void InvokeConfigureServices(Assembly assembly, IServiceCollection services)
	{
		var type =
			assembly.GetType(_entryPoint.TypeName, throwOnError: false)
			?? throw new InvalidOperationException($"Type '{_entryPoint.TypeName}' was not found in the app assembly.");

		var method =
			type.GetMethod(
				_entryPoint.MethodName,
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				[typeof(IServiceCollection), typeof(IConfiguration), typeof(IWebAssemblyHostEnvironment)]
			)
			?? throw new InvalidOperationException(
				$"Method '{_entryPoint.TypeName}.{_entryPoint.MethodName}' (with the original signature) was not found in the app assembly."
			);

		Invoke(method, [services, _configuration, _environment]);
	}

	/// <summary>
	/// The renderer caches the cascading values that were registered as services (<c>AddCascadingValue</c>) when it is created.
	/// Point it to the ones of the new generation, otherwise cascading parameters would be fed values of the old types.
	/// </summary>
	private void UpdateRendererCascadingValueSuppliers(AppGeneration generation)
	{
		if (_renderer is null)
		{
			return;
		}

		var supplierType = typeof(CascadingValueSource<>).Assembly.GetType(CascadingValueSupplierTypeName, throwOnError: true)!;
		var suppliers = generation.Services.GetServices(supplierType).ToList();
		var array = Array.CreateInstance(supplierType, suppliers.Count);
		for (var i = 0; i < suppliers.Count; i++)
		{
			array.SetValue(suppliers[i], i);
		}

		GetCascadingValueSuppliersField().SetValue(_renderer, array);
	}
}
