using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

namespace BlazorHMR.Server.Deltas;

/// <summary>
/// Computes Edit-and-Continue deltas: changes that can be applied to the running app in place (so all state is kept).
/// <para>
/// This uses Roslyn's hot reload service, the same engine that "dotnet watch" uses. It's internal to Roslyn (only visible to dotnet-watch),
/// so it's driven through reflection. If its shape ever changes, <see cref="TryCreate"/> returns null, and every change becomes a full swap.
/// </para>
/// </summary>
[SuppressMessage(
	"Major Code Smell",
	"S3011:Reflection should not be used to increase accessibility",
	Justification = "Roslyn's hot reload service is internal, see class docs"
)]
internal sealed class DeltaSession
{
	private const string ServiceTypeName =
		"Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService, Microsoft.CodeAnalysis.ExternalAccess.HotReload";

	private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private readonly object _service;
	private readonly MethodInfo _startSession;
	private readonly MethodInfo _getUpdates;
	private readonly MethodInfo _commitUpdate;
	private readonly MethodInfo _discardUpdate;
	private readonly MethodInfo _endSession;
	private readonly MethodInfo _capabilitiesChanged;
	private readonly Type _runningProjectInfoType;

	private ImmutableArray<string> _capabilities = [];
	private bool _started;

	private DeltaSession(object service, Type serviceType)
	{
		_service = service;
		_startSession = serviceType.GetMethod("StartSessionAsync", AnyInstance)!;
		_getUpdates = serviceType.GetMethod("GetUpdatesAsync", AnyInstance)!;
		_commitUpdate = serviceType.GetMethod("CommitUpdate", AnyInstance)!;
		_discardUpdate = serviceType.GetMethod("DiscardUpdate", AnyInstance)!;
		_endSession = serviceType.GetMethod("EndSession", AnyInstance)!;
		_capabilitiesChanged = serviceType.GetMethod("CapabilitiesChanged", AnyInstance)!;
		_runningProjectInfoType = serviceType.GetNestedType("RunningProjectInfo", BindingFlags.Public | BindingFlags.NonPublic)!;
	}

	/// <summary>
	/// Whether we know what the runtime in the client can apply (reported by the client when it connects).
	/// </summary>
	public bool HasCapabilities => !_capabilities.IsDefaultOrEmpty;

	/// <summary>
	/// Creates the session if the internal service exists. The service asks for a callback that returns the client runtime's
	/// capabilities (which arrive over the WebSocket, after the session has started), hence the captured <c>session</c>.
	/// </summary>
	public static DeltaSession? TryCreate(SolutionServices services)
	{
		try
		{
			var serviceType = Type.GetType(ServiceTypeName, throwOnError: true)!;
			DeltaSession? session = null;
			Func<ValueTask<ImmutableArray<string>>> capabilities = () => ValueTask.FromResult(session!._capabilities);
			var constructor =
				serviceType.GetConstructor(AnyInstance, [typeof(SolutionServices), typeof(Func<ValueTask<ImmutableArray<string>>>)])
				?? throw new MissingMethodException(serviceType.FullName, ".ctor");

			session = new DeltaSession(constructor.Invoke([services, capabilities]), serviceType);
			return session;
		}
		catch (Exception ex) when (ex is TypeLoadException or MissingMethodException or FileNotFoundException or TargetInvocationException)
		{
			// The internal API is not present in this SDK/Roslyn version. Full swaps still work, so don't fail the server.
			Log.Warning($"Edit-and-Continue is not available (every change will restart the app): {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Rude edits have ids like ENC0020; anything else that's an error means the code doesn't compile.
	/// </summary>
	public static bool IsCompileError(Diagnostic diagnostic) =>
		diagnostic.Severity == DiagnosticSeverity.Error && !diagnostic.Id.StartsWith("ENC", StringComparison.Ordinal);

	/// <summary>
	/// Tells Roslyn what the connected runtime can apply. If a session is already running, notify it so it can re-evaluate.
	/// </summary>
	public void SetCapabilities(string capabilities)
	{
		_capabilities = [.. capabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
		if (_started)
		{
			_capabilitiesChanged.Invoke(_service, null);
		}
	}

	/// <summary>
	/// (Re)starts the session: <paramref name="solution"/> must match what the client is running (via the projects' output paths).
	/// </summary>
	public async Task StartAsync(Solution solution)
	{
		if (_started)
		{
			_endSession.Invoke(_service, null);
		}

		await ((Task)_startSession.Invoke(_service, [solution, CancellationToken.None])!).ConfigureAwait(false);
		_started = true;
	}

	/// <summary>
	/// Asks Roslyn for the changes since the session's baseline. The result is a small internal object graph, read through
	/// reflection; its "Status" tells us whether to apply the deltas, do nothing, or resume with a full swap.
	/// </summary>
	public async Task<DeltaResult> GetUpdatesAsync(Solution solution, IEnumerable<ProjectId> runningProjects)
	{
		var running = CreateRunningProjects(runningProjects);
		var task = (Task)_getUpdates.Invoke(_service, [solution, running, CancellationToken.None])!;
		await task.ConfigureAwait(false);

		var updates = task.GetType().GetProperty("Result")!.GetValue(task)!;
		var updatesType = updates.GetType();
		var status = updatesType.GetProperty("Status")!.GetValue(updates)!.ToString();

		var diagnostics = ((IEnumerable<Diagnostic>)updatesType.GetProperty("PersistentDiagnostics")!.GetValue(updates)!).ToList();
		foreach (var transient in (IEnumerable)updatesType.GetProperty("TransientDiagnostics")!.GetValue(updates)!)
		{
			diagnostics.AddRange((IEnumerable<Diagnostic>)transient.GetType().GetField("Item2")!.GetValue(transient)!);
		}

		var projectUpdates = ((IEnumerable)updatesType.GetProperty("ProjectUpdates")!.GetValue(updates)!)
			.Cast<object>()
			.Select(ToUpdate)
			.ToList();
		var restart =
			((ICollection)updatesType.GetProperty("ProjectsToRestart")!.GetValue(updates)!).Count > 0
			|| ((ICollection)updatesType.GetProperty("ProjectsToRebuild")!.GetValue(updates)!).Count > 0;

		var result = status switch
		{
			"NoChangesToApply" when !restart => DeltaStatus.NoChanges,
			"ReadyToApply" when !restart => DeltaStatus.Ready,
			_ when diagnostics.Any(IsCompileError) => DeltaStatus.CompileErrors,
			_ => DeltaStatus.RestartRequired,
		};

		// Roslyn keeps the update pending whenever it could emit deltas, also when some projects need a restart: we don't use it then.
		if (status == "ReadyToApply" && result != DeltaStatus.Ready)
		{
			Discard();
		}

		return new DeltaResult(result, projectUpdates, diagnostics);
	}

	/// <summary>
	/// After <see cref="DeltaStatus.Ready"/>: the deltas have been sent, the solution is the new baseline for the next changes.
	/// </summary>
	public void Commit() => _commitUpdate.Invoke(_service, null);

	/// <summary>
	/// After <see cref="DeltaStatus.Ready"/>: the deltas are not used (a full swap happens instead).
	/// </summary>
	public void Discard() => _discardUpdate.Invoke(_service, null);

	/// <summary>
	/// Copies the internal ProjectUpdate into our own record, so the rest of the server doesn't have to know Roslyn's types.
	/// </summary>
	private static DeltaUpdate ToUpdate(object update)
	{
		var type = update.GetType();
		T Field<T>(string name) => (T)type.GetField(name)!.GetValue(update)!;

		return new DeltaUpdate(
			Field<Guid>("ModuleId"),
			Field<ProjectId>("ProjectId"),
			[.. Field<ImmutableArray<byte>>("MetadataDelta")],
			[.. Field<ImmutableArray<byte>>("ILDelta")],
			[.. Field<ImmutableArray<byte>>("PdbDelta")],
			[.. Field<ImmutableArray<int>>("UpdatedTypes")]
		);
	}

	/// <summary>
	/// ImmutableDictionary&lt;ProjectId, RunningProjectInfo&gt;, with RunningProjectInfo being internal.
	/// </summary>
	private object CreateRunningProjects(IEnumerable<ProjectId> projects)
	{
		var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(ProjectId), _runningProjectInfoType);
		var pairs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pairType))!;
		foreach (var project in projects)
		{
			var info = Activator.CreateInstance(_runningProjectInfoType);
			// Changes that don't affect anything currently loaded (e.g. a method nobody called yet), or in a project whose source no
			// longer matches the build output, can be reported as "no changes" and silently dropped. Ask Roslyn to request a restart
			// instead, so the server does a full swap. Older Roslyn versions don't expose this property and keep the old behavior.
			_runningProjectInfoType.GetProperty("RestartWhenChangesHaveNoEffect")?.SetValue(info, true);
			pairs.Add(Activator.CreateInstance(pairType, project, info));
		}

		var createRange = typeof(ImmutableDictionary)
			.GetMethods()
			.Single(m => m.Name == nameof(ImmutableDictionary.CreateRange) && m.GetParameters().Length == 1)
			.MakeGenericMethod(typeof(ProjectId), _runningProjectInfoType);

		return createRange.Invoke(null, [pairs])!;
	}
}
