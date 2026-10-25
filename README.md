# Dtr.Blazor.HotReload

Hot reload for Blazor WebAssembly apps that applies (almost) any change within ~1-2 seconds, without rebuilding, restarting,
or reloading the page.

Two packages:

| Package | What |
| --- | --- |
| `Dtr.Blazor.HotReload` | The browser side: a library for the app, plus build targets. Reference it in Debug builds only. |
| `Dtr.Blazor.HotReload.Server` | The hot reload server, as a .NET tool (`dtr-blazor-hotreload`). Watches and compiles the app, and talks to the browser. |

Requires .NET 10 (SDK and app).

## How it works

The approach is the same as [s&box's hotload](https://github.com/Facepunch/sbox-public): a complete new version of the app is compiled and
loaded next to the old one, as a new **app generation**.

### Browser (`Dtr.Blazor.HotReload`)

- A new generation's assemblies are loaded together into a fresh `AssemblyLoadContext`. Their dependencies (the framework, third-party
  libraries) come from the default context, so those types are shared.
- A new DI container is built by calling `ConfigureServices` from the *new* assembly, and swapped in behind the service provider that Blazor uses.
  Services registered on the builder before `UseHotReload()` (framework services such as `NavigationManager`, `IJSRuntime` and logging)
  are shared by all generations.
- The root is re-rendered (`HotReloadRoot` takes the place of the app's root component) with the new root component, which replaces the
  whole component tree. An error boundary around it shows unhandled exceptions, and an overlay shows the hot reload status.

## Building the packages

```sh
dotnet test Dtr.Blazor.HotReload.slnx
dotnet pack Dtr.Blazor.HotReload.slnx -c Release    # -> artifacts/*.nupkg
```

Bump `VersionPrefix` in `Directory.Build.props` for every release: NuGet caches packages by version, so a repacked package with the same version is not picked up.
