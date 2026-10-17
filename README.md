# Dtr.Blazor.HotReload

Hot reload for Blazor WebAssembly apps that applies (almost) any change within ~1-2 seconds, without rebuilding, restarting,
or reloading the page. Most changes are applied in place, keeping all state; for the rest, a new version of the app is swapped in.
Changes in the libraries the app uses (its project references) are hot reloaded too.

Two packages:

| Package | What |
| --- | --- |
| `Dtr.Blazor.HotReload` | The browser side: a library for the app, plus build targets. Reference it in Debug builds only. |
| `Dtr.Blazor.HotReload.Server` | The hot reload server, as a .NET tool (`dtr-blazor-hotreload`). Watches and compiles the app, and talks to the browser. |

Requires .NET 10 (SDK and app).

## Setup

1. Reference the library from the Blazor WebAssembly app, in Debug builds only:

   ```xml
   <ItemGroup Condition="'$(Configuration)' == 'Debug'">
     <PackageReference Include="Dtr.Blazor.HotReload" Version="0.1.1" />
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

Server options:

| Option | Default |
| --- | --- |
| `--project <path>` | The only `.csproj` in the current directory |
| `--port <port>` | `2009` |
| `--devserver` | Off: run the dev server yourself (and restart it after regular builds) |
| `--launch-profile <name>` | The first profile in `Properties/launchSettings.json` |
| `--devserver-url <url>` | The launch profile's `applicationUrl` |

Browser side configuration (all optional), in the app's `wwwroot/appsettings.json`:

```json
"HotReload": { "ServerUrl": "http://127.0.0.1:2009/", "MaxGenerations": 30 }
```

## How it works

The approach is the same as [s&box's hotload](https://github.com/Facepunch/sbox-public), which has a fast path and a full path:

1. **In place** (fast path, keeps all state): the change is turned into an Edit-and-Continue delta, and the running assemblies are patched.
   This is the same engine `dotnet watch` uses. It covers changes to method bodies, Razor markup and lambdas, and most added members and types.
2. **Full swap** (for everything else, e.g. "rude edits" like removing members, or changes to service registrations): a complete new version
   of the app is compiled and loaded next to the old one, as a new **app generation**, which starts the app over.

### Server (`dtr-blazor-hotreload`, on the dev machine)

- Keeps a Roslyn workspace with the app and the libraries it ships to the browser (its project references).
  - For the app it reuses the exact compiler command line of the last regular build (captured by the package's build targets): the same references,
    the same source generators (including Razor), the same editorconfigs, and so on.
  - For the libraries it asks MSBuild for their command lines with a "design-time" compile (the way IDEs do it), so the libraries need no changes.
  - Roslyn, the workspace layer and the Edit-and-Continue engine are loaded from the installed SDK (its Roslyn and dotnet-watch),
    so they always match the SDK's Razor generator.
- On every save, it tries to compute a delta against what the browser runs (the build output, or the last full swap).
  If that's not possible, it compiles a full swap: the app plus every library that changed since the last regular build (and libraries that depend on those).
- Changes in files that contain `IServiceCollection` always become a full swap: patching them in place wouldn't rebuild the DI container.
- Everything goes to the browser through a WebSocket; a reloaded page catches up (latest generation, plus the deltas on top of it).

### Browser (`Dtr.Blazor.HotReload`)

- `Dtr.Blazor.HotReload.lib.module.js` (a Blazor JavaScript initializer) starts the .NET runtime with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, so it accepts deltas.
- **Deltas** are applied with `MetadataUpdater.ApplyUpdate`; then the `[MetadataUpdateHandler]`s run (Blazor re-renders all components).
  If a delta leaves stale code behind (e.g. a changed method signature with unchanged callers, which then throw a `HotReloadException`),
  the page asks for a full swap by itself.
- **Full swaps** load the new assemblies together into a fresh `AssemblyLoadContext` (so the new app binds to the new versions of its libraries), then:
  - create a new DI container by calling `ConfigureServices` from the *new* assembly, then `InitializeServices`;
  - swap that container in, behind the service provider that Blazor uses;
  - re-render the root (`HotReloadRoot` takes the place of the app's root component) with the new root component.

## What you get

| Change | Result |
| --- | --- |
| Method bodies, Razor markup, lambdas, added fields/methods/types (in the app or its libraries) | In place, all state kept (~0.5 s) |
| Other changes in the app or its libraries: removed/renamed members, changed signatures, renamed enum values, DI registrations, ... | Full swap, app restarted (~1-2 s) |
| `wwwroot/**/*.css` | Hot (stylesheet is re-fetched) |
| Other `wwwroot` files (`index.html`, `app.js`, ...) | Page reload |
| Compile error (in any of the projects) | Overlay with the errors; the app keeps running the previous version |
| Unhandled exception while rendering | Shown in place of the app; fix it and save |
| Packages, csproj, or anything generated at build time only | Needs a regular build. The server notices, restarts the dev server (with `--devserver`), and reloads the page |

The overlay tells which one happened ("applied in place, state kept", or "app restarted").

**Full swaps don't keep state**: the new version starts the app over, in-memory component and service state (including static fields) is reset.
If a library needs static setup at startup, put it in `InitializeServices`, which runs for every generation.
The URL, local storage, cookies and the login are always kept.

## Limitations

- Old assemblies can't be unloaded in the browser. After 30 full swaps (`HotReload:MaxGenerations`), the page is reloaded once to free memory.
- Code in `Main` outside `ConfigureServices` / `InitializeServices` (culture, environment variables, logging providers) only runs on a page reload.
- An in-place update doesn't re-run constructors or field initializers of existing objects (same as `dotnet watch`). Save the file with another change, or reload the page, to start them over.
- Breakpoints only bind to the assembly the page started with. Reload the page to debug the latest code.
- Blazor WebAssembly only (not Blazor Server, nor hybrid/desktop hosts).
- Relies on internals: Roslyn's hot reload service (only visible to dotnet-watch, driven through reflection), and a few Blazor internals
  (`RenderHandle._renderer`, `Renderer.ServiceProviderCascadingValueSuppliers`).
  If an SDK/Blazor update changes those, hot reload falls back (full swaps only) or reports a clear error at startup.

## Files it leaves behind

- `dtr-hotreload.rsp` / `dtr-hotreload.txt` in the intermediate (obj) directories of the app and its libraries: their compiler command lines.
- `dtr-hotreload/` in the app's intermediate directory: the baselines of full swaps (what the deltas are computed against).

## Building the packages

```sh
dotnet test Dtr.Blazor.HotReload.slnx
dotnet pack Dtr.Blazor.HotReload.slnx -c Release    # -> artifacts/*.nupkg
```

Bump `VersionPrefix` in `Directory.Build.props` for every release: NuGet caches packages by version, so a repacked package with the same version is not picked up.
