using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Dtr.Blazor.HotReload.State;

/// <summary>
/// Converts objects of the previous generation's types into objects of the new generation's types (same full names, new assemblies),
/// field by field. A compact version of the "upgraders" of s&amp;box's hotload.
/// <para>
/// Best effort: whatever can't be converted (e.g. a field whose type changed) is left at the new object's default.
/// Objects of types that are not hot reloaded (strings, framework types, unchanged libraries) are shared as-is.
/// </para>
/// </summary>
[SuppressMessage(
	"Major Code Smell",
	"S3011:Reflection should not be used to increase accessibility",
	Justification = "Copying private state is the point"
)]
internal sealed class StateConverter
{
	private const BindingFlags InstanceFields =
		BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private readonly IReadOnlyDictionary<string, Assembly> _newAssemblies;
	private readonly Dictionary<object, object?> _converted = new(ReferenceEqualityComparer.Instance);

	public StateConverter(IReadOnlyDictionary<string, Assembly> newAssemblies) => _newAssemblies = newAssemblies;

	/// <summary>
	/// Fields that could not be carried over.
	/// </summary>
	public int Failures { get; private set; }

	/// <summary>
	/// Whether the type comes from the previous version of one of the assemblies that were swapped.
	/// </summary>
	public bool IsOld(Type type) =>
		type.Assembly.GetName().Name is { } name && _newAssemblies.TryGetValue(name, out var assembly) && type.Assembly != assembly;

	/// <summary>
	/// The new version of a type (or the type itself, if it wasn't swapped). Null if the type no longer exists.
	/// </summary>
	public Type? MapType(Type type)
	{
		if (type.IsArray)
		{
			var element = MapType(type.GetElementType()!);
			if (element is null)
			{
				return null;
			}

			return type.IsSZArray ? element.MakeArrayType() : element.MakeArrayType(type.GetArrayRank());
		}

		if (type.IsGenericType && !type.IsGenericTypeDefinition)
		{
			var definition = MapType(type.GetGenericTypeDefinition());
			var arguments = type.GetGenericArguments().Select(MapType).ToArray();
			return definition is null || arguments.Any(a => a is null) ? null : definition.MakeGenericType(arguments!);
		}

		if (!IsOld(type))
		{
			return type;
		}

		return _newAssemblies[type.Assembly.GetName().Name!].GetType(type.FullName!, throwOnError: false);
	}

	/// <summary>
	/// From now on, <paramref name="oldInstance"/> converts to <paramref name="newInstance"/> (used for services: references to an old
	/// service become references to the new generation's instance of that service).
	/// </summary>
	public void Map(object oldInstance, object newInstance) => _converted[oldInstance] = newInstance;

	public bool TryConvert(object? value, Type targetType, out object? result)
	{
		result = null;
		if (value is null)
		{
			return true;
		}

		if (_converted.TryGetValue(value, out var cached))
		{
			result = cached;
			return cached is null || targetType.IsInstanceOfType(cached);
		}

		var type = value.GetType();
		if (IsDropped(value))
		{
			return false;
		}

		if (value is Type typeValue)
		{
			result = MapType(typeValue);
			return result is not null;
		}

		if (!InvolvesOld(type))
		{
			result = value;
			return targetType.IsInstanceOfType(value);
		}

		var newType = MapType(type);
		if (newType is null || !targetType.IsAssignableFrom(newType))
		{
			return false;
		}

		result = type switch
		{
			{ IsEnum: true } => ConvertEnum(value, newType),
			{ IsArray: true } => ConvertArray((Array)value, newType),
			{ IsGenericType: true } when IsCollection(type) => ConvertCollection(value, newType),
			_ when IsOld(type) => ConvertObject(value, newType),

			// A framework generic with swapped type arguments that we don't know how to rebuild (e.g. a Lazy<T>, a Task<T>).
			_ => null,
		};

		return result is not null;
	}

	/// <summary>
	/// Copies the fields declared by the old types in <paramref name="source"/>'s hierarchy, into the matching fields of <paramref name="target"/>.
	/// </summary>
	public int CopyFields(object source, object target, Func<FieldInfo, bool>? skip = null)
	{
		_converted[source] = target;
		var copied = 0;

		for (var level = source.GetType(); level is not null && level != typeof(object); level = level.BaseType)
		{
			// Framework base classes (e.g. ComponentBase) are not ours to copy.
			if (!IsOld(level) || MapType(level) is not { } newLevel)
			{
				continue;
			}

			foreach (var field in level.GetFields(InstanceFields))
			{
				if (skip?.Invoke(field) == true)
				{
					continue;
				}

				var newField = newLevel.GetField(field.Name, InstanceFields);
				if (newField is null || newField.IsLiteral)
				{
					continue;
				}

				if (TryConvert(field.GetValue(source), newField.FieldType, out var value))
				{
					newField.SetValue(target, value);
					copied++;
				}
				else
				{
					Failures++;
				}
			}
		}

		return copied;
	}

	/// <summary>
	/// Things that are tied to the previous generation, or to a specific moment, and are better recreated by the new code.
	/// </summary>
	private static bool IsDropped(object value) =>
		value
			is Delegate
				or Task
				or CancellationTokenSource
				or Timer
				or IComponent
				or IJSObjectReference
				or IServiceProvider
				or ElementReference
				or WeakReference
		|| (value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(DotNetObjectReference<>));

	private static bool IsCollection(Type type)
	{
		var definition = type.GetGenericTypeDefinition();
		return definition == typeof(List<>) || definition == typeof(HashSet<>) || definition == typeof(Dictionary<,>);
	}

	private static object ConvertEnum(object value, Type newType)
	{
		var name = Enum.GetName(value.GetType(), value);
		return name is not null && Enum.IsDefined(newType, name)
			? Enum.Parse(newType, name)
			: Enum.ToObject(newType, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
	}

	private bool InvolvesOld(Type type) =>
		IsOld(type)
		|| (type.HasElementType && InvolvesOld(type.GetElementType()!))
		|| (type.IsGenericType && type.GetGenericArguments().Any(InvolvesOld));

	private Array ConvertArray(Array array, Type newType)
	{
		var elementType = newType.GetElementType()!;
		var result = Array.CreateInstance(elementType, array.Length);
		_converted[array] = result;
		for (var i = 0; i < array.Length; i++)
		{
			if (TryConvert(array.GetValue(i), elementType, out var element))
			{
				result.SetValue(element, i);
			}
			else
			{
				Failures++;
			}
		}

		return result;
	}

	private object ConvertCollection(object collection, Type newType)
	{
		var result = Activator.CreateInstance(newType)!;
		_converted[collection] = result;
		var arguments = newType.GetGenericArguments();

		if (collection is IDictionary dictionary)
		{
			var target = (IDictionary)result;
			foreach (DictionaryEntry entry in dictionary)
			{
				if (
					TryConvert(entry.Key, arguments[0], out var key)
					&& key is not null
					&& TryConvert(entry.Value, arguments[1], out var value)
				)
				{
					target[key] = value;
				}
				else
				{
					Failures++;
				}
			}

			return result;
		}

		var add = newType.GetMethod("Add", [arguments[0]])!;
		foreach (var item in (IEnumerable)collection)
		{
			if (TryConvert(item, arguments[0], out var converted))
			{
				add.Invoke(result, [converted]);
			}
			else
			{
				Failures++;
			}
		}

		return result;
	}

	private object ConvertObject(object value, Type newType)
	{
		// The parameterless constructor (if any) gives fields that are new in this version their initial values.
		object result;
		if (newType.IsValueType)
		{
			result = Activator.CreateInstance(newType)!;
		}
		else
		{
			var constructor = newType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
			try
			{
				result = constructor?.Invoke(null) ?? RuntimeHelpers.GetUninitializedObject(newType);
			}
			catch (TargetInvocationException)
			{
				result = RuntimeHelpers.GetUninitializedObject(newType);
			}
		}

		CopyFields(value, result);
		return result;
	}
}
