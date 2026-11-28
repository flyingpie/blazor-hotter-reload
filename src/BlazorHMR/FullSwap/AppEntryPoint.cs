namespace BlazorHMR.FullSwap;

/// <summary>
/// Pointer to a "Startup"-style class's "ConfigureServices"- and "Init"-methods, that set up DI and start the app.<br/>
/// This is stored as strings, so we can call these on initial start, but also on subsequent generations.
/// </summary>
/// <param name="StartupTypeName">Full name of the type that declares the methods.</param>
/// <param name="RegisterServicesMethodName">Static method with signature <c>(IServiceCollection)</c> that registers the app services.</param>
/// <param name="InitializeMethodName">
/// Optional static method with signature <c>(IServiceProvider)</c>, called once the services of a generation are built, to set up
/// static state (e.g. <c>Log.Setup(...)</c>).
/// </param>
public sealed record AppEntryPoint(string StartupTypeName, string RegisterServicesMethodName, string? InitializeMethodName = null);
