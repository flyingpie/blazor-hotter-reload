using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;

namespace Dtr.Blazor.HotReload.State;

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
internal sealed class StateCarryOver
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
	/// Takes note of the previous generation's components (in creation order), before they are replaced.
	/// </summary>
	public static StateCarryOver Capture(IEnumerable<IComponent> oldComponents, IReadOnlyDictionary<string, Assembly> newAssemblies)
	{
		ArgumentNullException.ThrowIfNull(oldComponents);

		var converter = new StateConverter(newAssemblies);
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
	/// The renderer's current components, in creation order.
	/// </summary>
	public static List<IComponent> GetComponents(Renderer? renderer)
	{
		var states = renderer is null
			? null
			: typeof(Renderer).GetField("_componentStateById", AnyInstance)?.GetValue(renderer) as Dictionary<int, ComponentState>;

		return states?.Values.OrderBy(s => s.ComponentId).Select(s => s.Component).ToList() ?? [];
	}

	/// <summary>
	/// Must be called for the new generation, before it renders.
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
	public void Restore(IComponent component)
	{
		ArgumentNullException.ThrowIfNull(component);

		IComponent? old;
		lock (_lock)
		{
			if (!_components.TryGetValue(component.GetType().FullName!, out var queue) || !queue.TryDequeue(out old))
			{
				return;
			}

			Components++;
		}

		_converter.CopyFields(old, component, IsSetByFramework);
	}

	/// <summary>
	/// Parameters, cascading parameters and injected services are set by Blazor, for the new instance.
	/// </summary>
	private static bool IsSetByFramework(FieldInfo field)
	{
		// Auto-property backing field: "<Name>k__BackingField".
		if (!field.Name.StartsWith('<'))
		{
			return false;
		}

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
	/// The services that a scope (or the root) created, from the DI container's internals.
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

			// Slot 0 is what a plain GetService returns (the last registration).
			if (slot == 0 && entry.Value is { } instance)
			{
				yield return (serviceType, serviceKey, instance);
			}
		}
	}

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

	private void CarryOverServices(AppGeneration previous, AppGeneration next)
	{
		var toCopy = new List<(object Old, object New)>();
		foreach (var scope in previous.EngineScopes)
		{
			foreach (var (serviceType, key, instance) in GetResolvedServices(scope))
			{
				if (
					serviceType.ContainsGenericParameters
					|| typeof(IServiceProvider).IsAssignableFrom(serviceType)
					|| serviceType == typeof(IServiceScopeFactory)
				)
				{
					continue;
				}

				if (_converter.MapType(serviceType) is not { } newServiceType)
				{
					continue;
				}

				object? newInstance;
				try
				{
					newInstance = key is null
						? next.Services.GetService(newServiceType)
						: next.Services.GetKeyedService(newServiceType, key);
				}
				catch (InvalidOperationException)
				{
					continue;
				}

				if (newInstance is null)
				{
					continue;
				}

				// References to the old service become references to the new one.
				_converter.Map(instance, newInstance);
				if (!ReferenceEquals(instance, newInstance) && _converter.IsOld(instance.GetType()))
				{
					toCopy.Add((instance, newInstance));
				}
			}
		}

		foreach (var (oldService, newService) in toCopy)
		{
			_converter.CopyFields(oldService, newService);
			Services++;
		}
	}

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
