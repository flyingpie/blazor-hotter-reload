namespace BlazorHMR.FullSwap;

/// <summary>
/// Pointer to a "Startup"-style class's "ConfigureServices"- and "Init"-methods, that set up DI and start the app.<br/>
/// This is stored as strings, so we can call these on initial start, but also on subsequent generations.
/// </summary>
public sealed record AppEntryPoint(string StartupTypeName, string RegisterServicesMethodName);
