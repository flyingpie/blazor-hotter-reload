// Blazor WebAssembly "JavaScript initializer": loaded automatically because of its name ({AssemblyName}.lib.module.js),
// so only in builds that reference the BlazorHMR package (which should be Debug builds only).
//
// Makes the .NET runtime accept in-place updates (Edit-and-Continue deltas, see DeltaApplier.cs).
// The SDK only does this when dotnet watch's browser-refresh script is on the page.
export function onRuntimeConfigLoaded(config) {
	config.environmentVariables ??= {};
	config.environmentVariables["DOTNET_MODIFIABLE_ASSEMBLIES"] ??= "debug";
}
