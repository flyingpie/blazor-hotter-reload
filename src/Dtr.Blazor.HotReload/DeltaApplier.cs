using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;

namespace Dtr.Blazor.HotReload;

/// <summary>
/// Applies in-place updates (Edit-and-Continue deltas) to the running assemblies, and tells the frameworks about it.
/// This is what the dotnet-watch agent does, minus dotnet-watch.
/// </summary>
[SuppressMessage(
	"Major Code Smell",
	"S3011:Reflection should not be used to increase accessibility",
	Justification = "The runtime capabilities are only available through a private method (dotnet-watch does the same)"
)]
internal static class DeltaApplier
{
	/// <summary>
	/// Whether the runtime supports in-place updates (it was started with DOTNET_MODIFIABLE_ASSEMBLIES=debug), and which kinds.
	/// </summary>
	public static (bool Supported, string Capabilities) GetCapabilities()
	{
		if (!MetadataUpdater.IsSupported)
		{
			return (false, string.Empty);
		}

		var method = typeof(MetadataUpdater).GetMethod("GetCapabilities", BindingFlags.NonPublic | BindingFlags.Static);
		return (true, method?.Invoke(null, null) as string ?? "Baseline");
	}

	/// <summary>
	/// Applies the updates, then lets every <c>[MetadataUpdateHandler]</c> know (e.g. Blazor, which re-renders all components).
	/// </summary>
	public static void Apply(IReadOnlyList<DeltaPayload> updates)
	{
		var assemblies = AppDomain.CurrentDomain.GetAssemblies();
		var updatedTypes = new List<Type>();

		foreach (var update in updates)
		{
			var assembly =
				Array.Find(assemblies, a => !a.IsDynamic && a.ManifestModule.ModuleVersionId == update.ModuleId)
				?? throw new InvalidOperationException($"No loaded assembly with module id {update.ModuleId}.");

			MetadataUpdater.ApplyUpdate(assembly, update.Metadata, update.IL, update.Pdb);

			foreach (var token in update.UpdatedTypes)
			{
				try
				{
					updatedTypes.Add(assembly.ManifestModule.ResolveType(token));
				}
				catch (ArgumentException)
				{
					// New type that can't be resolved by token, the handlers get the others.
				}
			}
		}

		InvokeHandlers(assemblies, [.. updatedTypes]);
	}

	private static void InvokeHandlers(Assembly[] assemblies, Type[] updatedTypes)
	{
		var handlers = assemblies
			.Where(a => !a.IsDynamic)
			.SelectMany(a => a.GetCustomAttributes<MetadataUpdateHandlerAttribute>())
			.Select(a => a.HandlerType)
			.Distinct()
			.ToList();

		foreach (var method in new[] { "ClearCache", "UpdateApplication" })
		{
			foreach (var handler in handlers)
			{
				var callback = handler.GetMethod(
					method,
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
					[typeof(Type[])]
				);
				try
				{
					callback?.Invoke(null, [updatedTypes]);
				}
				catch (TargetInvocationException ex)
				{
					Console.WriteLine($"[hotreload] {handler.FullName}.{method} failed: {ex.InnerException}");
				}
			}
		}
	}
}
