namespace Dtr.Blazor.HotReload;

/// <summary>
/// Where to find the service configuration in (every version of) the app assembly.
/// </summary>
internal sealed record AppEntryPoint(string TypeName, string MethodName);
