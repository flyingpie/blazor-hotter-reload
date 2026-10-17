using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dtr.Blazor.HotReload.UnitTest;

/// <summary>
/// Compiles two versions of the same assembly, and loads them side by side (like two generations of the app).
/// </summary>
internal sealed class TestGenerations
{
	public const string AssemblyName = "Dtr.HotReloadTest";

	// Includes this assembly, for types that are never swapped (like a library that didn't change).
	private static readonly MetadataReference[] References =
	[
		.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Append(typeof(TestGenerations).Assembly.Location)
			.Distinct(StringComparer.Ordinal)
			.Select(path => MetadataReference.CreateFromFile(path)),
	];

	public TestGenerations(string oldSource, string newSource)
	{
		Old = Load("old", oldSource);
		New = Load("new", newSource);
	}

	public Assembly Old { get; }

	public Assembly New { get; }

	public IReadOnlyDictionary<string, Assembly> NewAssemblies => new Dictionary<string, Assembly> { [AssemblyName] = New };

	public object CreateOld(string typeName) => Activator.CreateInstance(Old.GetType(typeName, throwOnError: true)!, nonPublic: true)!;

	public Type NewType(string typeName) => New.GetType(typeName, throwOnError: true)!;

	private static Assembly Load(string name, string source)
	{
		var compilation = CSharpCompilation.Create(
			AssemblyName,
			[CSharpSyntaxTree.ParseText(source)],
			References,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
		);

		using var stream = new MemoryStream();
		var result = compilation.Emit(stream);
		if (!result.Success)
		{
			throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
		}

		stream.Position = 0;
		return new AssemblyLoadContext(name, isCollectible: true).LoadFromStream(stream);
	}
}
