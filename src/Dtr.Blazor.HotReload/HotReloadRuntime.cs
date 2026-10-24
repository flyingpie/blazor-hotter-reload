using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Dtr.Blazor.HotReload.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
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
	private readonly SemaphoreSlim _swapLock = new(1, 1);
	private readonly List<HotReloadRoot> _roots = [];
	private readonly SwappableServiceProvider _rootProvider;

	private IReadOnlyList<ServiceDescriptor> _hostDescriptors = [];
	private IServiceProvider? _hostRoot;
	private ServiceProvider? _hostProvider;
	private AsyncServiceScope? _hostScope;
	private bool _managesHostScope;
	private AppGeneration? _current;
	private Renderer? _renderer;
	private HotReloadClient? _client;
	private int _restartRequestedFor = -1;
	private List<IComponent>? _componentsBeforeDelta;

	internal HotReloadRuntime(AppEntryPoint entryPoint, Assembly initialAssembly, HotReloadOptions options)
	{
		_entryPoint = entryPoint;
		_initialAssembly = initialAssembly;
		_rootProvider = new SwappableServiceProvider(this, isRoot: true);
		Options = options;
	}

	internal IServiceProvider RootProvider => _rootProvider;

	/// <summary>
	/// The host owns the page's scope (desktop), so a new page gets a new scope and a rebuilt generation.
	/// Browser hosts build a single scope themselves, and we only forward it to the generations.
	/// </summary>
	internal bool ManagesHostScope => _managesHostScope;

	private IServiceProvider HostServices =>
		(_hostScope ?? throw new InvalidOperationException("Hot reload has not been initialized yet.")).ServiceProvider;

	internal event Action? StatusChanged;

	public HotReloadOptions Options { get; }

	public AppGeneration Current => _current ?? throw new InvalidOperationException("Hot reload has not been initialized yet.");

	/// <summary>
	/// What the overlay shows.
	/// </summary>
	public HotReloadStatus? Status { get; private set; }

	internal bool HasOverlayOwner { get; set; }

	/// <summary>
	/// After a full swap: the state of the previous generation's components, for the new components to pick up when they are created.
	/// </summary>
	internal StateCarryOver? PendingState { get; private set; }

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

			// Build and load the next generation before touching the current one: if anything fails here, the running app is
			// still on the previous generation and keeps working.
			var loadContext = new GenerationLoadContext(number, assemblies);
			var newAssembly = loadContext.Get(_initialAssembly.GetName().Name!);

			var next = BuildGeneration(number, newAssembly, loadContext.AssembliesByName);
			var previous = Current;

			// Carry over what state we can: statics and services now, components when they are created (see HotReloadComponentActivator).
			// If an in-place update left stale code behind, the error boundary has already thrown the components away: use the ones from
			// before that update.
			var oldComponents =
				_restartRequestedFor == previous.Number && _componentsBeforeDelta is not null
					? _componentsBeforeDelta
					: StateCarryOver.GetComponents(_renderer);
			_componentsBeforeDelta = null;
			var state = StateCarryOver.Capture(oldComponents, next.Assemblies);
			try
			{
				state.CarryOverServicesAndStatics(previous, next);
			}
			catch (Exception ex)
				when (ex is TargetInvocationException or InvalidOperationException or ArgumentException or MemberAccessException)
			{
				Console.WriteLine($"[hotreload] Could not carry over services/statics: {ex}");
			}

			InvokeInitializeServices(next);

			// From here on, service resolutions and renders use the new generation.
			_current = next;
			PendingState = state;
			UpdateRendererCascadingValueSuppliers(next);

			// Re-render all roots with the root components of the new generation (which disposes the old component tree).
			foreach (var root in _roots.ToList())
			{
				await root.RenderGenerationAsync().ConfigureAwait(true);
			}

			// The new tree is rendered, so nothing refers to the previous generation's components anymore.
			await previous.DisposeAsync().ConfigureAwait(true);

			stopwatch.Stop();
			var carried = $"{state.Components} components, {state.Services} services, {state.Statics} statics";
			Console.WriteLine(
				$"[hotreload] Applied generation {number} ({string.Join(", ", assemblies.Select(a => a.Name))}) in {stopwatch.ElapsedMilliseconds} ms, "
					+ $"carried over {carried} ({state.Failures} fields could not be carried over)"
			);
			SetStatus(
				new HotReloadStatus(
					HotReloadStatusKind.Applied,
					$"Hot reload applied, app restarted, state carried over where possible ({carried}; {stopwatch.ElapsedMilliseconds} ms)",
					[]
				)
			);
		}
		finally
		{
			_swapLock.Release();
		}
	}

	/// <summary>
	/// Patches the running assemblies in place (Edit-and-Continue deltas): nothing is recreated, so all state is kept.
	/// Blazor re-renders all components afterwards.
	/// </summary>
	public async Task ApplyDeltaAsync(IReadOnlyList<DeltaPayload> updates)
	{
		ArgumentNullException.ThrowIfNull(updates);

		await _swapLock.WaitAsync().ConfigureAwait(true);
		try
		{
			var stopwatch = Stopwatch.StartNew();

			// In case the update leaves stale code behind, and a full swap is needed after all (see OnUnhandledException):
			// keep a snapshot of the current component tree. By the time the swap happens, the error boundary may have thrown
			// parts of it away, but this snapshot still has them.
			_componentsBeforeDelta = StateCarryOver.GetComponents(_renderer);
			DeltaApplier.Apply(updates);
			stopwatch.Stop();

			Console.WriteLine($"[hotreload] Applied update in place ({updates.Count} assemblies) in {stopwatch.ElapsedMilliseconds} ms");
			SetStatus(
				new HotReloadStatus(
					HotReloadStatusKind.Applied,
					$"Hot reload applied in place, state kept ({stopwatch.ElapsedMilliseconds} ms)",
					[]
				)
			);
		}
		finally
		{
			_swapLock.Release();
		}
	}

	/// <summary>
	/// Stops the client first, then disposes the generation and the host container (in reverse order of creation).
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		if (_client is not null)
		{
			await _client.DisposeAsync().ConfigureAwait(false);
		}

		if (_current is not null)
		{
			await _current.DisposeAsync().ConfigureAwait(false);
		}

		if (_hostScope is { } hostScope)
		{
			await hostScope.DisposeAsync().ConfigureAwait(false);
		}

		if (_hostProvider is not null)
		{
			await _hostProvider.DisposeAsync().ConfigureAwait(false);
		}

		_swapLock.Dispose();
	}

	/// <summary>
	/// Called by the container factory, with the services that were registered on the host builder (i.e. the "host" tier).
	/// The browser host builds the container itself, and returns a provider that forwards to the current generation.
	/// </summary>
	internal IServiceProvider Initialize(IServiceCollection hostServices)
	{
		_hostDescriptors = [.. hostServices];
		_hostProvider = hostServices.BuildServiceProvider();
		_hostScope = _hostProvider.CreateAsyncScope();
		_managesHostScope = false;

		// Generation 0 is built the exact same way as all the next ones, so problems with that show up right away.
		_current = BuildGeneration(
			0,
			_initialAssembly,
			new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase) { [_initialAssembly.GetName().Name!] = _initialAssembly }
		);

		return _rootProvider;
	}

	/// <summary>
	/// Called by the container factory of a desktop host that builds the host provider itself (PhotinoX): the host owns the page scope.
	/// Generation 0 is built right away, because the host resolves services from the root provider before the first page attaches;
	/// <see cref="CreateHostScope"/> rebuilds it on a real host scope when the page attaches.
	/// </summary>
	internal IServiceProvider InitializeDesktop(IServiceCollection hostServices)
	{
		_hostDescriptors = [.. hostServices];
		_hostProvider = hostServices.BuildServiceProvider();
		_hostRoot = _hostProvider;
		_hostScope = _hostProvider.CreateAsyncScope();
		_managesHostScope = true;

		_current = BuildGeneration(
			0,
			_initialAssembly,
			new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase) { [_initialAssembly.GetName().Name!] = _initialAssembly }
		);

		return _rootProvider;
	}

	/// <summary>
	/// Called by the desktop host: the host provider already exists, and owns the page scope. That scope is created lazily
	/// (see <see cref="CreateHostScope"/>) because the WebView manager constructs a page scope when the page attaches.
	/// </summary>
	internal void InitializeHost(IServiceProvider hostRoot, IReadOnlyList<ServiceDescriptor> hostDescriptors)
	{
		_hostRoot = hostRoot;
		_hostDescriptors = hostDescriptors;
		_managesHostScope = true;
	}

	/// <summary>
	/// Desktop page attach/reload: the WebView creates a new scope, so give it a scope of the real host provider, and rebuild the
	/// current generation on top of it. The assemblies are kept: deltas that were applied in place stay applied, and the client keeps
	/// its sequence counters (so it won't try to apply them again).
	/// </summary>
	internal SwappableServiceProvider CreateHostScope()
	{
		var hostRoot = _hostRoot ?? throw new InvalidOperationException("The host service provider has not been set.");
		var scope = hostRoot.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
		var previousScope = _hostScope;
		var previous = _current;

		_hostScope = scope;
		_renderer = null;
		_roots.Clear();
		HasOverlayOwner = false;
		PendingState = null;
		_componentsBeforeDelta = null;
		_restartRequestedFor = -1;
		SetStatus(null);

		try
		{
			_current = previous is null
				? BuildGeneration(
					0,
					_initialAssembly,
					new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase) { [_initialAssembly.GetName().Name!] = _initialAssembly }
				)
				// The assemblies stay as they are, and the page's component tree starts over, so the generation count starts over too.
				: BuildGeneration(0, previous.Assembly, previous.Assemblies);
		}
		catch
		{
			_hostScope = previousScope;
			_current = previous;
			scope.Dispose();
			throw;
		}

		if (previous is not null)
		{
			Console.WriteLine("[hotreload] Page reloaded, current code ready");
			_ = DisposeGenerationAsync(previous, previousScope);
		}

		// The desktop host starts generation 0 itself (the app cannot: it only sees the host's services), so initialize it here.
		InvokeInitializeServices(_current);

		_client?.Reattach(scope.ServiceProvider);

		return new SwappableServiceProvider(this, isRoot: false);
	}

	private static async Task DisposeGenerationAsync(AppGeneration generation, AsyncServiceScope? scope)
	{
		try
		{
			await generation.DisposeAsync().ConfigureAwait(false);
			if (scope is { } hostScope)
			{
				await hostScope.DisposeAsync().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[hotreload] Could not dispose the previous page: {ex}");
		}
	}

	/// <summary>
	/// Called when a <see cref="HotReloadRoot"/> attaches. The first root supplies the renderer and starts the server connection;
	/// additional roots only take part in re-renders.
	/// </summary>
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

		if (_client is null)
		{
			_client = new HotReloadClient(this, HostServices);
			_client.Start();
		}
	}

	internal void Detach(HotReloadRoot root) => _roots.Remove(root);

	/// <summary>
	/// Resolves the root component by name in the current generation. Generations load their own copies of the app and changed
	/// libraries, so the type must not be cached: every swap may bring a new version.
	/// </summary>
	internal Type GetRootType(string typeName)
	{
		foreach (var assembly in Current.Assemblies.Values)
		{
			if (assembly.GetType(typeName, throwOnError: false) is { } type)
			{
				return type;
			}
		}

		// The root component doesn't have to be in the app assembly: a desktop host's root usually lives in a referenced library.
		foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
		{
			if (assembly.GetType(typeName, throwOnError: false) is { } type)
			{
				return type;
			}
		}

		throw new InvalidOperationException($"Root component '{typeName}' was not found in generation {Current.Number}.");
	}

	internal void SetStatus(HotReloadStatus? status)
	{
		Status = status;
		StatusChanged?.Invoke();
	}

	/// <summary>
	/// The "fresh start" button: a full compile of the latest code, then a page reload (see <see cref="HotReloadClient.RequestFreshStartAsync"/>).
	/// </summary>
	internal Task RequestFreshStartAsync() => _client?.RequestFreshStartAsync() ?? Task.CompletedTask;

	/// <summary>
	/// Called for exceptions that end up in the app's error boundary.
	/// In-place updates can leave code behind that is out of date: e.g. when a method's signature changes, unchanged callers still call
	/// the old version, which now throws a HotReloadException. A full swap (compiled from scratch) fixes that.
	/// </summary>
	internal void OnUnhandledException(Exception exception)
	{
		var isStaleCode = false;
		for (var current = exception; current is not null; current = current.InnerException)
		{
			isStaleCode |= current.GetType().FullName == "System.Runtime.CompilerServices.HotReloadException";
		}

		if (!isStaleCode || _client is null || _restartRequestedFor == Current.Number)
		{
			return;
		}

		_restartRequestedFor = Current.Number;
		Console.WriteLine("[hotreload] Code is out of date after an in-place update, restarting the app");
		SetStatus(
			new HotReloadStatus(
				HotReloadStatusKind.Compiling,
				"Hot reload: code changed too much to update in place, restarting the app...",
				[]
			)
		);
		_ = _client.RequestRestartAsync();
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

	/// <summary>
	/// The only way to reach the renderer's cascading value suppliers is its private backing field; fail loudly if Blazor changed it.
	/// </summary>
	private static FieldInfo GetCascadingValueSuppliersField() =>
		typeof(Renderer).GetField(CascadingValueSuppliersFieldName, BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException(
			$"Field 'Renderer.{CascadingValueSuppliersFieldName}' was not found; Blazor internals have changed, Dtr.Blazor.HotReload needs to be updated."
		);

	/// <summary>
	/// Builds one generation: a container with the host-tier services forwarded, plus the services that the app registers.
	/// </summary>
	private AppGeneration BuildGeneration(int number, Assembly assembly, IReadOnlyDictionary<string, Assembly> assemblies)
	{
		var services = new ServiceCollection();

		ForwardHostServices(services);
		InvokeConfigureServices(assembly, services);

		return new AppGeneration(number, assembly, assemblies, services.BuildServiceProvider());
	}

	/// <summary>
	/// Host-tier services are shared by all generations. Singletons and scoped services are resolved from the host tier, and registered as
	/// instances (so a generation's container never disposes them). Transients and open generics are just registered again.
	/// </summary>
	private void ForwardHostServices(IServiceCollection services)
	{
		var hostServices = HostServices;
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

			// Resolve<IEnumerable<T>>() returns all registrations for T in registration order, so the index of this descriptor
			// among the registrations for the same service type/key gives the instance that belongs to it.
			var index = _hostDescriptors
				.Where(d => d.ServiceType == descriptor.ServiceType && Equals(d.ServiceKey, descriptor.ServiceKey))
				.ToList()
				.IndexOf(descriptor);
			if (index < 0 || index >= instances.Count || instances[index] is not { } instance)
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
	/// Finds the service configuration method in this generation's app assembly (by the name and signature captured at startup)
	/// and runs it against the new container.
	/// </summary>
	private void InvokeConfigureServices(Assembly assembly, IServiceCollection services)
	{
		var type =
			assembly.GetType(_entryPoint.TypeName, throwOnError: false)
			?? throw new InvalidOperationException($"Type '{_entryPoint.TypeName}' was not found in the app assembly.");

		var parameterTypes = new[] { typeof(IServiceCollection) }.Concat(_entryPoint.ParameterTypes).ToArray();
		var method =
			type.GetMethod(_entryPoint.MethodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, parameterTypes)
			?? throw new InvalidOperationException(
				$"Method '{_entryPoint.TypeName}.{_entryPoint.MethodName}' (with the original signature) was not found in the app assembly."
			);

		Invoke(method, [services, .. _entryPoint.Arguments]);
	}

	/// <summary>
	/// Static state in a new generation's assemblies starts out fresh (e.g. <c>Dtr.Utils.Log</c>), give the app a chance to set it up.
	/// Generation 0 is initialized by the app itself (browser), or by <see cref="CreateHostScope"/> when the page attaches (desktop).
	/// </summary>
	private void InvokeInitializeServices(AppGeneration generation)
	{
		if (_entryPoint.InitializeMethodName is null)
		{
			return;
		}

		var method =
			generation
				.Assembly.GetType(_entryPoint.TypeName, throwOnError: false)
				?.GetMethod(
					_entryPoint.InitializeMethodName,
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
					[typeof(IServiceProvider)]
				)
			?? throw new InvalidOperationException(
				$"Method '{_entryPoint.TypeName}.{_entryPoint.InitializeMethodName}' (with the original signature) was not found in the app assembly."
			);

		Invoke(method, [generation.Services]);
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
