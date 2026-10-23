# Dtr.Blazor.HotReload

Hot reload for Blazor WebAssembly and Photino desktop apps that applies (almost) any change within ~1-2 seconds, without rebuilding, restarting,
or reloading the page. Most changes are applied in place, keeping all state; for the rest, a new version of the app is swapped in,
carrying over what state it can. Changes in the libraries the app uses (its project references) are hot reloaded too.

Three packages:

| Package | What |
| --- | --- |
| `Dtr.Blazor.HotReload` | The browser side: a library for the Blazor WebAssembly app, plus build targets. Reference it in Debug builds only. |
| `Dtr.Blazor.HotReload.Desktop` | The desktop side, for a Photino BlazorWebView host: the same runtime, with the host integration. Reference it in Debug builds only. |
| `Dtr.Blazor.HotReload.Server` | The hot reload server, as a .NET tool (`dtr-blazor-hotreload`). Watches and compiles the app, and talks to the client. |

Requires .NET 10 (SDK and app).

## Setup

1. Reference the library from the Blazor WebAssembly app, in Debug builds only:

   ```xml
   <ItemGroup Condition="'$(Configuration)' == 'Debug'">
     <PackageReference Include="Dtr.Blazor.HotReload" Version="0.1.6" />
   </ItemGroup>
   ```

   Its build targets define `HOTRELOAD` (Debug builds only; opt out with `-p:DtrHotReload=false`), and capture the app's compiler command line for the server.

2. Split the service registrations out of `Program.Main` into a static method, and let hot reload call it:

   ```csharp
   #if HOTRELOAD
   using Dtr.Blazor.HotReload;
   #endif

   public static class Program
   {
       public static async Task Main(string[] args)
       {
           var builder = WebAssemblyHostBuilder.CreateDefault(args);
           builder.RootComponents.Add<App>("#app");
           builder.RootComponents.Add<HeadOutlet>("head::after");

   #if HOTRELOAD
           builder.UseHotReload(ConfigureServices, InitializeServices);
   #else
           ConfigureServices(builder.Services, builder.Configuration, builder.HostEnvironment);
   #endif

           var host = builder.Build();
           InitializeServices(host.Services);
           await host.RunAsync();
       }

       // Called again for every new version of the app (so it must be static, and keep its name and signature).
       public static void ConfigureServices(IServiceCollection services, IConfiguration config, IWebAssemblyHostEnvironment env)
       {
           services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(env.BaseAddress) });
           // ...
       }

       // Optional: static setup that depends on the services (e.g. a static logger factory). Also called for every new version.
       public static void InitializeServices(IServiceProvider services)
       {
       }
   }
   ```

   `UseHotReload` must come after the root components are added. Services registered on the builder *before* it (framework defaults, logging providers)
   are shared by all versions of the app; everything in `ConfigureServices` is created fresh for every full swap.

3. Install the server as a (local) tool, and run it next to the app:

   ```sh
   dotnet new tool-manifest   # if there isn't one yet
   dotnet tool install Dtr.Blazor.HotReload.Server
   dotnet build path/to/App.csproj -c Debug
   dotnet tool run dtr-blazor-hotreload --project path/to/App.csproj --devserver
   ```

   With `--devserver` the server also runs the app's dev server (`dotnet run`, with the URL from `Properties/launchSettings.json`),
   and restarts it after regular builds. Then open the app, edit any `.razor` / `.cs` file of the app or its libraries, and save.

### Desktop (Photino Blazor)

A desktop host uses the same runtime through `Dtr.Blazor.HotReload.Desktop`: reference that instead of the browser package (in Debug builds only),
keep its own host services on the builder, and move the app's services into a static method, just like the browser app:

```csharp
#if HOTRELOAD
using Dtr.Blazor.HotReload;
#endif

var appBuilder = PhotinoBlazorAppBuilder.CreateDefault();
appBuilder.Services.AddSingleton(config); // host services, shared by all generations
appBuilder.RootComponents.Add<App>("#app");

#if HOTRELOAD
appBuilder.UseHotReload(config, ConfigureServices, InitializeServices);
#else
ConfigureServices(appBuilder.Services, config);
#endif

var app = appBuilder.Build();
#if !HOTRELOAD
InitializeServices(app.Services); // with hot reload, the runtime initializes each generation (it sees the app services)
#endif
app.Run();

// Called again for every new version of the app (so it must be static, and keep its name and signature).
public static void ConfigureServices(IServiceCollection services, IConfiguration config)
{
    services.AddSingleton(config);
    // ...
}
```

The desktop host owns the page's service scope. A page reload (including the `⟳` fresh start button) therefore rebuilds the current generation on a
new scope without restarting the process, so in-place updates stay applied. Run the app with `DOTNET_MODIFIABLE_ASSEMBLIES=Debug` (e.g. in the
`Photino` launch profile) to accept deltas. With `--devserver` the server starts `dotnet run` and restarts it after regular builds; a desktop launch
profile has no `applicationUrl`, so no `--devserver-url` is needed.

Server options:

| Option | Default |
| --- | --- |
| `--project <path>` | The only `.csproj` in the current directory |
| `--port <port>` | `2009` |
| `--devserver` | Off: run the dev server yourself (and restart it after regular builds) |
| `--launch-profile <name>` | The first profile in `Properties/launchSettings.json` |
| `--devserver-url <url>` | The launch profile's `applicationUrl` (not needed for desktop apps) |

Client side configuration (all optional), in the app's `wwwroot/appsettings.json` (browser) or the app's normal configuration (desktop):

```json
"HotReload": { "ServerUrl": "http://127.0.0.1:2009/", "MaxGenerations": 30 }
```

## How it works

The approach is the same as [s&box's hotload](https://github.com/Facepunch/sbox-public), which has a fast path and a full path:

1. **In place** (fast path, keeps all state): the change is turned into an Edit-and-Continue delta, and the running assemblies are patched.
   This is the same engine `dotnet watch` uses. It covers changes to method bodies, Razor markup and lambdas, and most added members and types.
2. **Full swap** (for everything else, e.g. "rude edits" like removing members, or changes to service registrations): a complete new version
   of the app is compiled and loaded next to the old one, as a new **app generation**. State is carried over where possible (see below).

### Server (`dtr-blazor-hotreload`, on the dev machine)

- Keeps a Roslyn workspace with the app and the libraries it ships (its project references).
  - For the app it reuses the exact compiler command line of the last regular build (captured by the package's build targets): the same references,
    the same source generators (including Razor), the same editorconfigs, and so on.
  - For the libraries it asks MSBuild for their command lines with a "design-time" compile (the way IDEs do it), so the libraries need no changes.
  - Roslyn, the workspace layer and the Edit-and-Continue engine are loaded from the installed SDK (its Roslyn and dotnet-watch),
    so they always match the SDK's Razor generator.
- On every save, it tries to compute a delta against what the client runs (the build output, or the last full swap).
  If that's not possible, it compiles a full swap: the app plus every library that changed since the last regular build (and libraries that depend on those).
- Changes in files that contain `IServiceCollection` always become a full swap: patching them in place wouldn't rebuild the DI container.
- Everything goes to the client through a WebSocket; a reloaded page catches up (latest generation, plus the deltas on top of it).

### Client runtime (`Dtr.Blazor.HotReload` / `.Desktop`)

- In the browser, `Dtr.Blazor.HotReload.lib.module.js` (a Blazor JavaScript initializer) starts the .NET runtime with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, so it accepts deltas.
  On the desktop, the launch profile has to set `DOTNET_MODIFIABLE_ASSEMBLIES=Debug`.
- **Deltas** are applied with `MetadataUpdater.ApplyUpdate`; then the `[MetadataUpdateHandler]`s run (Blazor re-renders all components).
  If a delta leaves stale code behind (e.g. a changed method signature with unchanged callers, which then throw a `HotReloadException`),
  the page asks for a full swap by itself.
- **Fresh start** (the `⟳` button at the bottom left, also in the overlay and on the error page): asks the server for a full swap,
  and reloads the page instead of swapping it in. The reloaded page catches up with that generation: everything starts over
  (DOM, JavaScript, state), running the latest code, without a regular build. On the desktop, that means a new page scope and a rebuilt
  generation in the same process, so in-place updates stay applied.
- **Full swaps** load the new assemblies together into a fresh `AssemblyLoadContext` (so the new app binds to the new versions of its libraries), then:
  - create a new DI container by calling `ConfigureServices` from the *new* assembly, then `InitializeServices`;
  - swap that container in, behind the service provider that Blazor uses;
  - carry over state, then re-render the root (`HotReloadRoot` takes the place of the app's root component) with the new root component.

## What you get

| Change | Result |
| --- | --- |
| Method bodies, Razor markup, lambdas, added fields/methods/types (in the app or its libraries) | In place, all state kept (~0.5 s) |
| Other changes in the app or its libraries: removed/renamed members, changed signatures, renamed enum values, DI registrations, ... | Full swap, state carried over where possible (~1-2 s) |
| `wwwroot/**/*.css` | Hot (stylesheet is re-fetched) |
| Other `wwwroot` files (`index.html`, `app.js`, ...) | Page reload (on the desktop: new page scope, same process) |
| Compile error (in any of the projects) | Overlay with the errors; the app keeps running the previous version |
| Unhandled exception while rendering | Shown in place of the app; fix it and save |
| The page got into a weird state | Click `⟳` (fresh start): recompiles everything in memory and reloads the page (~2-4 s) |
| Packages, csproj, or anything generated at build time only | Needs a regular build. The server notices, restarts the dev server (with `--devserver`), and reloads the page |

The overlay tells which one happened ("applied in place, state kept", or "app restarted, state carried over ...").

### State carry-over (full swaps)

Best effort, like s&box's object migration. Values are converted to the new versions of their types, field by field (by name):

- **Static fields** of the swapped types.
- **Services**: the app's singletons and scoped services that the previous generation created get the fields of their predecessors.
  References to old services are replaced by the new instances (so nothing holds on to the old container).
- **Components**: when the new tree is rendered, each new component is matched with an old one of the same type, in creation order,
  and gets its fields before parameters are set and `OnInitialized` runs. `[Parameter]`, `[CascadingParameter]` and `[Inject]` members are left to Blazor.
  Components that load data in `OnInitialized(Async)` still do so (and overwrite what was carried over), but UI state like selections and unsaved input is kept.

Not carried over: fields whose type changed in an incompatible way, delegates/event handlers (the new code subscribes again), tasks,
`CancellationTokenSource`s, JS object references, `ElementReference`s, component references (`@ref`), and objects of types that no longer exist.
Third-party services start fresh. If the component structure changed, the matching by creation order can be off.

If a library needs static setup at startup, put it in `InitializeServices`, which runs for every generation.
The URL, local storage, cookies and the login are always kept.

## Limitations

- Old assemblies can't be unloaded. In the browser, after 30 full swaps (`HotReload:MaxGenerations`) the page is reloaded once to free memory.
  On the desktop, a page reload keeps the process (and the loaded assemblies); restart the app to free them.
- Code in `Main` outside `ConfigureServices` / `InitializeServices` (culture, environment variables, logging providers) only runs on a page reload (browser), or on a process restart (desktop).
- An in-place update doesn't re-run constructors or field initializers of existing objects (same as `dotnet watch`). Save the file with another change, or reload the page, to start them over.
- Breakpoints only bind to the assembly the process started with. Reload the page to debug the latest code (the desktop keeps the process).
- Blazor WebAssembly (browser) and Photino BlazorWebView (desktop) hosts. Not Blazor Server, MAUI, or other WebView hosts.
- Relies on internals: Roslyn's hot reload service (only visible to dotnet-watch, driven through reflection), and a few Blazor and DI internals
  (`RenderHandle._renderer`, `Renderer.ServiceProviderCascadingValueSuppliers`, `Renderer._componentStateById`, the container's resolved services).
  If an SDK/Blazor update changes those, hot reload falls back (full swaps only, or no state carry-over) or reports a clear error at startup.

## Files it leaves behind

- `dtr-hotreload.rsp` / `dtr-hotreload.txt` in the intermediate (obj) directories of the app and its libraries: their compiler command lines.
- `dtr-hotreload/` in the app's intermediate directory: the baselines of full swaps (what the deltas are computed against).

## Building the packages

```sh
dotnet test Dtr.Blazor.HotReload.slnx
dotnet pack Dtr.Blazor.HotReload.slnx -c Release    # -> artifacts/*.nupkg
```

Bump `VersionPrefix` in `Directory.Build.props` for every release: NuGet caches packages by version, so a repacked package with the same version is not picked up.
`test/Dtr.Blazor.HotReload.UnitTest` covers the state conversion (two versions of the same types, compiled and loaded side by side).
