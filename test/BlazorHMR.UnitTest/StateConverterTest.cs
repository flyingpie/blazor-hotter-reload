using System.Reflection;
using BlazorHMR.FullSwap;

namespace BlazorHMR.UnitTest;

[TestClass]
public class StateConverterTest
{
	private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	[TestMethod]
	public void CopiesPublicPrivateAndReadonlyFields()
	{
		const string source = """
			public class Model
			{
				public int Count;
				private string _name = "";
				private readonly int _id;
				public string Text { get; set; } = "";
				public Model() { }
				public void Set() { Count = 3; _name = "n"; Text = "t"; System.Runtime.CompilerServices.Unsafe.AsRef(in _id) = 7; }
			}
			""";
		var generations = new TestGenerations(source, source);
		var old = generations.CreateOld("Model");
		old.GetType().GetMethod("Set")!.Invoke(old, null);

		var converted = Convert(generations, old, "Model");

		Assert.AreEqual(generations.NewType("Model"), converted.GetType());
		Assert.AreEqual(3, Get(converted, "Count"));
		Assert.AreEqual("n", Get(converted, "_name"));
		Assert.AreEqual(7, Get(converted, "_id"));
		Assert.AreEqual("t", Get(converted, "<Text>k__BackingField"));
	}

	[TestMethod]
	public void NewFieldsGetTheirInitialValue()
	{
		var generations = new TestGenerations(
			"public class Model { public int Count = 1; }",
			"public class Model { public int Count = 1; public string Added = \"initial\"; }"
		);
		var old = generations.CreateOld("Model");
		Set(old, "Count", 5);

		var converted = Convert(generations, old, "Model");

		Assert.AreEqual(5, Get(converted, "Count"));
		Assert.AreEqual("initial", Get(converted, "Added"));
	}

	[TestMethod]
	public void CopiesFieldsOfBaseTypesThatWereNotSwapped()
	{
		// Like an app class that derives from a class in a library that didn't change: no parameterless constructor, so the new object
		// is not constructed.
		const string source = """
			public class Model(string name) : BlazorHMR.UnitTest.UnswappedBase(name)
			{
				public int Count;
			}
			""";
		var generations = new TestGenerations(source, source);
		var old = Activator.CreateInstance(generations.Old.GetType("Model", throwOnError: true)!, "entry")!;
		Set(old, "Count", 5);

		var converted = (UnswappedBase)Convert(generations, old, "Model");

		Assert.AreEqual("entry", converted.Name);
		Assert.AreEqual(5, Get(converted, "Count"));
	}

	[TestMethod]
	public void FieldWithAnIncompatibleNewTypeIsLeftAtItsDefault()
	{
		var generations = new TestGenerations(
			"public class Model { public int Value = 5; public string Kept = \"\"; }",
			"public class Model { public string Value = \"default\"; public string Kept = \"\"; }"
		);
		var old = generations.CreateOld("Model");
		Set(old, "Kept", "kept");
		var converter = new StateConverter(generations.NewAssemblies);

		Assert.IsTrue(converter.TryConvert(old, generations.NewType("Model"), out var converted));

		Assert.AreEqual("default", Get(converted!, "Value"));
		Assert.AreEqual("kept", Get(converted!, "Kept"));
		Assert.AreEqual(1, converter.Failures);
	}

	[TestMethod]
	public void EnumsAreConvertedByNameWithANumericFallback()
	{
		var generations = new TestGenerations(
			"public enum Kind { A = 1, B = 2 } public class Model { public Kind First = Kind.A; public Kind Second = Kind.B; }",
			"public enum Kind { B = 5, Renamed = 1 } public class Model { public Kind First; public Kind Second; }"
		);
		var old = generations.CreateOld("Model");

		var converted = Convert(generations, old, "Model");

		// "A" no longer exists: falls back to its number (1, now "Renamed"); "B" exists by name (now 5).
		Assert.AreEqual("Renamed", Get(converted, "First")!.ToString());
		Assert.AreEqual("B", Get(converted, "Second")!.ToString());
	}

	[TestMethod]
	public void CollectionsAreRebuiltWithTheNewTypes()
	{
		const string source = """
			using System.Collections.Generic;
			public class Item { public int Value; }
			public class Model
			{
				public List<Item> List = new();
				public Dictionary<string, Item> Dictionary = new();
				public Item[] Array = [];
				public List<int> Numbers = new();
			}
			""";
		var generations = new TestGenerations(source, source);
		var old = generations.CreateOld("Model");
		var item = generations.CreateOld("Item");
		Set(item, "Value", 42);
		((System.Collections.IList)Get(old, "List")!).Add(item);
		((System.Collections.IDictionary)Get(old, "Dictionary")!)["key"] = item;
		var array = Array.CreateInstance(item.GetType(), 1);
		array.SetValue(item, 0);
		Set(old, "Array", array);
		var numbers = Get(old, "Numbers");

		var converted = Convert(generations, old, "Model");

		var newItem = ((System.Collections.IList)Get(converted, "List")!)[0]!;
		Assert.AreEqual(generations.NewType("Item"), newItem.GetType());
		Assert.AreEqual(42, Get(newItem, "Value"));
		Assert.AreSame(newItem, ((System.Collections.IDictionary)Get(converted, "Dictionary")!)["key"], "The same old item converts to the same new item");
		Assert.AreSame(newItem, ((Array)Get(converted, "Array")!).GetValue(0));
		Assert.AreSame(numbers, Get(converted, "Numbers"), "Collections without swapped types are shared");
	}

	[TestMethod]
	public void CyclesArePreserved()
	{
		const string source = "public class Node { public Node? Next; public int Value; }";
		var generations = new TestGenerations(source, source);
		var first = generations.CreateOld("Node");
		var second = generations.CreateOld("Node");
		Set(first, "Next", second);
		Set(second, "Next", first);
		Set(second, "Value", 2);

		var converted = Convert(generations, first, "Node");

		var next = Get(converted, "Next")!;
		Assert.AreEqual(2, Get(next, "Value"));
		Assert.AreSame(converted, Get(next, "Next"));
	}

	[TestMethod]
	public void DelegatesAreDropped()
	{
		const string source = "public class Model { public System.Action? Callback; public int Value; }";
		var generations = new TestGenerations(source, source);
		var old = generations.CreateOld("Model");
		Set(old, "Callback", new Action(() => { }));
		Set(old, "Value", 1);

		var converted = Convert(generations, old, "Model");

		Assert.IsNull(Get(converted, "Callback"));
		Assert.AreEqual(1, Get(converted, "Value"));
	}

	[TestMethod]
	public void MappedInstancesReplaceTheirOldCounterparts()
	{
		const string source = "public class Service { } public class Model { public Service? Service; }";
		var generations = new TestGenerations(source, source);
		var oldService = generations.CreateOld("Service");
		var newService = Activator.CreateInstance(generations.NewType("Service"))!;
		var old = generations.CreateOld("Model");
		Set(old, "Service", oldService);
		var converter = new StateConverter(generations.NewAssemblies);
		converter.Map(oldService, newService);

		Assert.IsTrue(converter.TryConvert(old, generations.NewType("Model"), out var converted));

		Assert.AreSame(newService, Get(converted!, "Service"));
	}

	[TestMethod]
	public void TypesThatNoLongerExistCannotBeConverted()
	{
		var generations = new TestGenerations("public class Gone { }", "public class Other { }");
		var converter = new StateConverter(generations.NewAssemblies);

		Assert.IsNull(converter.MapType(generations.Old.GetType("Gone")!));
		Assert.IsFalse(converter.TryConvert(generations.CreateOld("Gone"), typeof(object), out _));
	}

	private static object Convert(TestGenerations generations, object old, string typeName)
	{
		var converter = new StateConverter(generations.NewAssemblies);
		Assert.IsTrue(converter.TryConvert(old, generations.NewType(typeName), out var converted));
		return converted!;
	}

	private static object? Get(object instance, string field) => instance.GetType().GetField(field, AnyInstance)!.GetValue(instance);

	private static void Set(object instance, string field, object? value) => instance.GetType().GetField(field, AnyInstance)!.SetValue(instance, value);
}
