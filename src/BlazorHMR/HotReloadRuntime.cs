using System.Reflection;
using System.Runtime.Loader;
using BlazorHMR.Bcl;
using BlazorHMR.Blazor;
using BlazorHMR.FullSwap;
using BlazorHMR.InPlace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR;

/// <summary>
/// Owns the current app generation, the host tier, and the shared state between the two ways a change can be applied.
/// <para>
/// The actual work is done by two explicit pipelines:
/// <see cref="FullSwapApplier"/> rebuilds the whole app from a new assembly (losing in-memory state, but able to apply any
/// change), and <see cref="InPlacePatchApplier"/> patches the running assemblies (keeping all state, but only for changes
/// the runtime can apply). They share the <see cref="PatchGate"/>, <see cref="RenderCoordinator"/>, and
/// <see cref="PatchRecoveryState"/> here, but otherwise have nothing in common.
/// </para>
/// </summary>
public sealed class HotReloadRuntime : IAsyncDisposable
{
	private readonly Assembly _initialAssembly;
	private readonly SwappableServiceProvider _rootProvider;
	private readonly HostTier _host = new();
	private readonly GenerationFactory _generations;

	private AppGeneration? _current;
	private HotReloadClient? _client;

	public HotReloadRuntime(AppEntryPoint entryPoint, Assembly initialAssembly)
	{
		_initialAssembly = initialAssembly;
		_generations = new GenerationFactory(entryPoint, _host);
		_rootProvider = new SwappableServiceProvider(this, isRoot: true);
		FullSwap = new FullSwapApplier(this);
		InPlace = new InPlacePatchApplier(this);
	}

	public IServiceProvider RootProvider => _rootProvider;

	/// <summary>
	/// The full-swap pipeline: replaces the app with a complete new generation.
	/// </summary>
	public FullSwapApplier FullSwap { get; }

	/// <summary>
	/// The in-place pipeline: patches the running assemblies and keeps all state.
	/// </summary>
	public InPlacePatchApplier InPlace { get; }

	/// <summary>
	/// Makes sure a full swap and an in-place patch never run at the same time.
	/// </summary>
	internal PatchGate Gate { get; } = new();

	/// <summary>
	/// The attached roots and renderer, shared by both pipelines.
	/// </summary>
	internal RenderCoordinator Render { get; } = new();

	/// <summary>
	/// What an in-place patch leaves behind for a possible recovery full swap.
	/// </summary>
	internal PatchRecoveryState Recovery { get; } = new();

	/// <summary>
	/// Builds generations. The full-swap pipeline uses it to create the next one.
	/// </summary>
	internal GenerationFactory Generations => _generations;

	internal Assembly InitialAssembly => _initialAssembly;

	public event Action? StatusChanged;

	public AppGeneration Current => _current ?? throw new InvalidOperationException("Hot reload has not been initialized yet.");

	/// <summary>
	/// What the overlay shows.
	/// </summary>
	public HotReloadStatus? Status { get; private set; }

	public bool HasOverlayOwner { get; set; }

	/// <summary>
	/// After a full swap: the state of the previous generation's components, for the new components to pick up when they are created.
	/// </summary>
	public StateCarryOver? PendingState { get; internal set; }

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

		await _host.DisposeAsync().ConfigureAwait(false);

		Gate.Dispose();
	}

	/// <summary>
	/// Called by the container factory of a desktop host that builds the host provider itself (PhotinoX): the host owns the page
	/// scope. Generation 0 is built right away, because the host resolves services from the root provider before the first page
	/// attaches; <see cref="CreateHostScope"/> rebuilds it on a real host scope when the page attaches.
	/// </summary>
	public IServiceProvider InitializeDesktop(IServiceCollection hostServices)
	{
		_host.InitializeDesktop(hostServices);
		_current = BuildInitialGeneration();

		return _rootProvider;
	}

	/// <summary>
	/// Called by a desktop host that already has its provider (Photino): the host provider exists and owns the page scope. That
	/// scope is created lazily (see <see cref="CreateHostScope"/>) because the WebView manager constructs a page scope when the
	/// page attaches.
	/// </summary>
	public void InitializeHost(IServiceProvider hostRoot, IReadOnlyList<ServiceDescriptor> hostDescriptors) =>
		_host.InitializeHost(hostRoot, hostDescriptors);

	/// <summary>
	/// Desktop page attach/reload: the WebView creates a new scope, so give it a scope of the real host provider, and rebuild the
	/// current generation on top of it. The assemblies are kept: patches that were applied in place stay applied, and the client keeps
	/// its sequence counters (so it won't try to apply them again).
	/// </summary>
	public SwappableServiceProvider CreateHostScope()
	{
		var previous = _current;
		var previousScope = _host.ReplaceScope();

		Render.Reset();
		HasOverlayOwner = false;
		PendingState = null;
		Recovery.Reset();
		SetStatus(null);

		try
		{
			// The assemblies stay as they are, and the page's component tree starts over, so the generation count starts over too.
			_current = previous is null
				? BuildInitialGeneration()
				: _generations.Build(0, previous.Assembly, previous.Assemblies);
		}
		catch
		{
			_host.RestoreScope(previousScope);
			_current = previous;
			throw;
		}

		if (previous is not null)
		{
			Console.WriteLine("[hotreload] Page reloaded, current code ready");
			_ = HostTier.DisposeGenerationAsync(previous, previousScope);
		}

		_client?.Reattach(_host.Services);

		return new SwappableServiceProvider(this, isRoot: false);
	}

	/// <summary>
	/// Called when a <see cref="HotReloadRoot"/> attaches. The first root supplies the renderer and starts the server connection;
	/// additional roots only take part in re-renders.
	/// </summary>
	public void Attach(HotReloadRoot root, Renderer renderer)
	{
		if (!Render.Attach(root, renderer))
		{
			return;
		}

		if (_client is null)
		{
			_client = new HotReloadClient(this, _host.Services);
			_client.Start();
		}
	}

	public void Detach(HotReloadRoot root) => Render.Detach(root);

	/// <summary>
	/// Resolves the root component by name in the current generation. Generations load their own copies of the app and changed
	/// libraries, so the type must not be cached: every swap may bring a new version.
	/// </summary>
	public Type GetRootType(string typeName)
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

	public void SetStatus(HotReloadStatus? status)
	{
		Status = status;
		StatusChanged?.Invoke();
	}

	/// <summary>
	/// The "fresh start" button: a full compile of the latest code, then a page reload (see <see cref="HotReloadClient.RequestFreshStartAsync"/>).
	/// </summary>
	public Task RequestFreshStartAsync() => _client?.RequestFreshStartAsync() ?? Task.CompletedTask;

	/// <summary>
	/// Called for exceptions that end up in the app's error boundary.
	/// In-place patches can leave code behind that is out of date: e.g. when a method's signature changes, unchanged callers still call
	/// the old version, which now throws a HotReloadException. A full swap (compiled from scratch) fixes that.
	/// </summary>
	public void OnUnhandledException(Exception exception)
	{
		var isStaleCode = false;
		for (var current = exception; current is not null; current = current.InnerException)
		{
			isStaleCode |= current.GetType().FullName == "System.Runtime.CompilerServices.HotReloadException";
		}

		if (!isStaleCode || _client is null || Recovery.IsRestartRequestedFor(Current.Number))
		{
			return;
		}

		Recovery.RequestRestart(Current.Number);
		Console.WriteLine("[hotreload] Code is out of date after an in-place patch, restarting the app");
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
	/// Makes <paramref name="generation"/> the one that service resolutions and renders use. Called by
	/// <see cref="FullSwapApplier"/> after it has built and prepared the new generation.
	/// </summary>
	internal void SetCurrentGeneration(AppGeneration generation) => _current = generation;

	/// <summary>
	/// Generation 0, built from the assembly the app booted with.
	/// </summary>
	private AppGeneration BuildInitialGeneration() =>
		_generations.Build(
			0,
			_initialAssembly,
			new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
			{
				[_initialAssembly.GetName().Name!] = _initialAssembly,
			}
		);
}
