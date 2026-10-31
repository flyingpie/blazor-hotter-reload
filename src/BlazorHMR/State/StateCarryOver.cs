using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorHMR.State;

/// <summary>
/// Carries state from the previous generation over to a new one, for full swaps (in-place updates keep all state anyway):
/// <list type="bullet">
/// <item>static fields of the swapped types;</item>
/// <item>the app's singleton and scoped services that the previous generation created;</item>
/// <item>component fields: new components are matched with old ones by type and creation order (when they are created, see
/// <see cref="HotReloadComponentActivator"/>), and get their fields before parameters are set and <c>OnInitialized</c> runs.</item>
/// </list>
/// Best effort, see <see cref="StateConverter"/>.
/// </summary>
[SuppressMessage(
	"Major Code Smell",
	"S3011:Reflection should not be used to increase accessibility",
	Justification = "Reads Blazor's and the DI container's internals, see class docs"
)]
[SuppressMessage("Usage", "BL0006:Do not use RenderTree types", Justification = "Needs the renderer's components")]
public sealed class StateCarryOver
{
	private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	private const BindingFlags StaticFields =
		BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private readonly StateConverter _converter;
	private readonly Dictionary<string, Queue<IComponent>> _components;
	private readonly Lock _lock = new();

	private StateCarryOver(StateConverter converter, Dictionary<string, Queue<IComponent>> components)
	{
		_converter = converter;
		_components = components;
	}

	public int Services { get; private set; }

	public int Statics { get; private set; }

	public int Components { get; private set; }

	public int Failures => _converter.Failures;

	/// <summary>
	/// Takes note of the previous generation's components (in creation order), before they are replaced. Only components from
	/// swapped assemblies need this; framework and unchanged-library components are recreated normally.
	/// </summary>
	public static StateCarryOver Capture(IEnumerable<IComponent> oldComponents, IReadOnlyDictionary<string, Assembly> newAssemblies)
	{
		ArgumentNullException.ThrowIfNull(oldComponents);

		var converter = new StateConverter(newAssemblies);

		// Components are matched by full type name, then by creation order within that type. The queues are consumed as the
		// renderer creates the new tree, so the first new X gets the state of the first old X, and so on.
		var components = new Dictionary<string, Queue<IComponent>>(StringComparer.Ordinal);

		foreach (var component in oldComponents)
		{
			var type = component.GetType();
			if (!converter.IsOld(type))
			{
				continue;
			}

			if (!components.TryGetValue(type.FullName!, out var queue))
			{
				components[type.FullName!] = queue = new Queue<IComponent>();
			}

			queue.Enqueue(component);
		}

		return new StateCarryOver(converter, components);
	}

	/// <summary>
	/// The renderer's current components, in creation order. Blazor's internal component state map is keyed by component id,
	/// which increases as components are created, so ordering by it gives the order the activator saw them.
	/// </summary>
	public static List<IComponent> GetComponents(Renderer? renderer)
	{
		var states = renderer is null
			? null
			: typeof(Renderer).GetField("_componentStateById", AnyInstance)?.GetValue(renderer) as Dictionary<int, ComponentState>;

		return states?.Values.OrderBy(s => s.ComponentId).Select(s => s.Component).ToList() ?? [];
	}

	/// <summary>
	/// Carries over services and statics from the previous generation to the new one. Must be called for the new generation,
	/// before it renders (components get their state when they are created).
	/// </summary>
	public void CarryOverServicesAndStatics(AppGeneration previous, AppGeneration next)
	{
		ArgumentNullException.ThrowIfNull(previous);
		ArgumentNullException.ThrowIfNull(next);

		CarryOverServices(previous, next);
		CarryOverStatics(previous, next);
	}

	/// <summary>
	/// Gives a newly created component the fields of its counterpart in the previous generation (same type, same creation order).
	/// </summary>
	/// <summary>
	/// Gives a newly created component the fields of the next unused component of the same type from the previous
	/// generation, if there is one.
	/// </summary>
	public void Restore(IComponent component)
	{
		ArgumentNullException.ThrowIfNull(component);

		IComponent? old;
		lock (_lock)
		{
			// A component with no counterpart (new in this version, or more instances than before) simply starts empty.
			if (!_components.TryGetValue(component.GetType().FullName!, out var queue) || !queue.TryDequeue(out old))
			{
				return;
			}

			Components++;
		}

		// Copy the old fields before Blazor sets parameters and calls OnInitialized, so the new component sees its old state.
		_converter.CopyFields(old, component, IsSetByFramework);
	}

	/// <summary>
	/// Parameters, cascading parameters and injected services are set by Blazor, for the new instance.
	/// </summary>
	private static bool IsSetByFramework(FieldInfo field)
	{
		// Auto-property backing field: "<Name>k__BackingField". Other compiler-generated fields (lambdas, state machines)
		// are just state and should be copied.
		if (!field.Name.StartsWith('<'))
		{
			return false;
		}

		// Parameters and [Inject] properties are assigned by Blazor for the new instance; copying the old values would either
		// be overwritten right after, or point at services from the previous generation.
		var propertyName = field.Name[1..field.Name.IndexOf('>', StringComparison.Ordinal)];
		var property = field.DeclaringType!.GetProperty(propertyName, AnyInstance | BindingFlags.DeclaredOnly);
		return property
				?.GetCustomAttributes(inherit: true)
				.Any(a =>
					a.GetType().Name is var name
					&& (name.Contains("Parameter", StringComparison.Ordinal) || name == nameof(InjectAttribute))
				) == true;
	}

	/// <summary>
	/// The services that a scope (or the root) created, from the DI container's internals. The container caches realized
	/// singletons/scoped instances in "ResolvedServices", keyed by service identifier and registration slot.
	/// </summary>
	private static IEnumerable<(Type ServiceType, object? Key, object Instance)> GetResolvedServices(object scope)
	{
		var resolved = scope.GetType().GetProperty("ResolvedServices", AnyInstance)?.GetValue(scope) as IDictionary;
		if (resolved is null)
		{
			yield break;
		}

		foreach (DictionaryEntry entry in resolved)
		{
			var keyType = entry.Key.GetType();
			var identifier = keyType.GetProperty("ServiceIdentifier", AnyInstance)!.GetValue(entry.Key)!;
			var slot = (int)keyType.GetProperty("Slot", AnyInstance)!.GetValue(entry.Key)!;
			var serviceType = (Type)identifier.GetType().GetProperty("ServiceType", AnyInstance)!.GetValue(identifier)!;
			var serviceKey = identifier.GetType().GetProperty("ServiceKey", AnyInstance)!.GetValue(identifier);

			// Slot 0 is what a plain GetService returns (the last registration). Other slots are earlier registrations for
			// the same service type, which are reachable through IEnumerable<T>; the new container will create those itself.
			if (slot == 0 && entry.Value is { } instance)
			{
				yield return (serviceType, serviceKey, instance);
			}
		}
	}

	/// <summary>
	/// All loadable types of an assembly. A generation may reference types from an assembly that isn't loaded (yet), which
	/// makes GetTypes throw; the exception still lists every type it could load.
	/// </summary>
	private static IEnumerable<Type> GetTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			return ex.Types.OfType<Type>();
		}
	}

	/// <summary>
	/// For every service instance that the previous generation resolved, resolves the new generation's instance and copies the
	/// old fields into it. The identity map (old instance -> new instance) is filled first, so references between services are
	/// converted too.
	/// </summary>
	private void CarryOverServices(AppGeneration previous, AppGeneration next)
	{
		var toCopy = new List<(object Old, object New)>();
		foreach (var scope in previous.EngineScopes)
		{
			foreach (var (serviceType, key, instance) in GetResolvedServices(scope))
			{
				// Services that represent the container itself can't be carried over; the new container has its own.
				if (
					serviceType.ContainsGenericParameters
					|| typeof(IServiceProvider).IsAssignableFrom(serviceType)
					|| serviceType == typeof(IServiceScopeFactory)
				)
				{
					continue;
				}

				// The type might no longer exist in the new generation (e.g. it was removed or renamed).
				if (_converter.MapType(serviceType) is not { } newServiceType)
				{
					continue;
				}

				// Resolve the new instance first: its constructor runs with the new code, then gets the old state copied in.
				object? newInstance;
				try
				{
					newInstance = key is null
						? next.Services.GetService(newServiceType)
						: ((IKeyedServiceProvider)next.Services).GetKeyedService(newServiceType, key);
				}
				catch (InvalidOperationException)
				{
					// A factory that needs something the new container can't provide (yet).
					continue;
				}

				if (newInstance is null)
				{
					continue;
				}

				// References to the old service become references to the new one (including references from other services
				// copied later, through the converter's identity map).
				_converter.Map(instance, newInstance);
				if (!ReferenceEquals(instance, newInstance) && _converter.IsOld(instance.GetType()))
				{
					toCopy.Add((instance, newInstance));
				}
			}
		}

		// Copy after collecting, so the identity map is complete before any field is converted.
		foreach (var (oldService, newService) in toCopy)
		{
			_converter.CopyFields(oldService, newService);
			Services++;
		}
	}

	/// <summary>
	/// Copies mutable static fields of swapped types. Static state is per assembly, so without this every generation would
	/// start with the field initializers (e.g. a logger or a global cache would be reset).
	/// </summary>
	private void CarryOverStatics(AppGeneration previous, AppGeneration next)
	{
		foreach (var (name, newAssembly) in next.Assemblies)
		{
			if (previous.GetAssembly(name) is not { } oldAssembly || oldAssembly == newAssembly)
			{
				continue;
			}

			foreach (var type in GetTypes(oldAssembly))
			{
				// Open generic types and compiler-generated types (state machines, lambdas, anonymous types) don't have a
				// stable one-to-one counterpart that is worth copying.
				if (
					type.ContainsGenericParameters
					|| type.Name.Contains('<', StringComparison.Ordinal)
					|| _converter.MapType(type) is not { } newType
				)
				{
					continue;
				}

				foreach (var field in type.GetFields(StaticFields).Where(f => !f.IsLiteral && !f.IsInitOnly))
				{
					var newField = newType.GetField(field.Name, StaticFields);
					if (newField is null || newField.IsLiteral || newField.IsInitOnly)
					{
						continue;
					}

					if (_converter.TryConvert(field.GetValue(null), newField.FieldType, out var value))
					{
						newField.SetValue(null, value);
						Statics++;
					}
				}
			}
		}
	}
}
