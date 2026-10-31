namespace BlazorHMR;

/// <summary>
/// Where to find the service configuration (and the optional initialization) in (every version of) the app assembly.
/// <para>
/// The shape of the methods is captured at startup, so the same method can be found in the new version of the app assembly.
/// The arguments (configuration, and for WebAssembly also the host environment) are captured too; only the service collection
/// is supplied per generation.
/// </para>
/// </summary>
public sealed record AppEntryPoint(
	string TypeName,
	string MethodName,
	Type[] ParameterTypes,
	object?[] Arguments,
	string? InitializeMethodName
);
