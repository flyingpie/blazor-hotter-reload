# Dtr.Blazor.HotReload

Hot reload for Blazor WebAssembly apps that applies (almost) any change within ~1-2 seconds, without rebuilding, restarting,
or reloading the page. Changes in the libraries the app uses (its project references) are hot reloaded too.

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
   are shared by all versions of the app; everything in `ConfigureServices` is created fresh for every new version.

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

The approach is the same as [s&box's hotload](https://github.com/Facepunch/sbox-public): a complete new version of the app is compiled and
loaded next to the old one, as a new **app generation**.

### Server (`dtr-blazor-hotreload`, on the dev machine)

- Keeps a Roslyn workspace with the app and the libraries it ships to the browser (its project references).
  - For the app it reuses the exact compiler command line of the last regular build (captured by the package's build targets): the same references,
    the same source generators (including Razor), the same editorconfigs, and so on.
  - For the libraries it asks MSBuild for their command lines with a "design-time" compile (the way IDEs do it), so the libraries need no changes.
  - Roslyn and the workspace layer are loaded from the installed SDK (its Roslyn and dotnet-watch), so they always match the SDK's Razor generator.
- On every save it recompiles (incrementally: only changed documents are re-parsed) the app plus every library that changed since the last
  regular build (and libraries that depend on those), emits them to memory, and tells the browser through a WebSocket.
  A reloaded page catches up with the latest generation.

### Browser (`Dtr.Blazor.HotReload`)

- A new generation's assemblies are loaded together into a fresh `AssemblyLoadContext`, so the new app binds to the new versions of its libraries. Their other dependencies (the framework, third-party
  libraries) come from the default context, so those types are shared.
- A new DI container is built by calling `ConfigureServices` from the *new* assembly (then `InitializeServices`), and swapped in behind the service provider that Blazor uses.
  Services registered on the builder before `UseHotReload()` (framework services such as `NavigationManager`, `IJSRuntime` and logging)
  are shared by all generations.
- The root is re-rendered (`HotReloadRoot` takes the place of the app's root component) with the new root component, which replaces the
  whole component tree. An error boundary around it shows unhandled exceptions, and an overlay shows the hot reload status.

## What you get

| Change | Result |
| --- | --- |
| `.razor` / `.cs` changes in the app or its libraries: markup, code, new/removed files, members, signatures, renamed enum values, DI registrations, ... | New version of the app (~1-2 s) |
| `wwwroot/**/*.css` | Hot (stylesheet is re-fetched) |
| Other `wwwroot` files (`index.html`, `app.js`, ...) | Page reload |
| Compile error | Overlay with the errors; the app keeps running the previous version |
| Unhandled exception while rendering | Shown in place of the app; fix it and save |
| Packages, csproj, or anything generated at build time only | Needs a regular build. The server notices, restarts the dev server (with `--devserver`), and reloads the page |

**State is not kept**: every new version starts the app over, in-memory component and service state (including static fields) is reset.
If a library needs static setup at startup, put it in `InitializeServices`, which runs for every generation.
The URL, local storage, cookies and the login are kept.

## Limitations

- Old assemblies can't be unloaded in the browser. After 30 new versions (`HotReload:MaxGenerations`), the page is reloaded once to free memory.
- Code in `Main` outside `ConfigureServices` / `InitializeServices` (culture, environment variables, logging providers) only runs on a page reload.
- Breakpoints only bind to the assembly the page started with. Reload the page to debug the latest code.
- Blazor WebAssembly only (not Blazor Server, nor hybrid/desktop hosts).
- Relies on a few Blazor internals (`RenderHandle._renderer`, `Renderer.ServiceProviderCascadingValueSuppliers`).
  If a Blazor update changes those, hot reload reports a clear error at startup.

## Files it leaves behind

- `dtr-hotreload.rsp` / `dtr-hotreload.txt` in the intermediate (obj) directories of the app and its libraries: their compiler command lines.

## Building the packages

```sh
dotnet test Dtr.Blazor.HotReload.slnx
dotnet pack Dtr.Blazor.HotReload.slnx -c Release    # -> artifacts/*.nupkg
```

Bump `VersionPrefix` in `Directory.Build.props` for every release: NuGet caches packages by version, so a repacked package with the same version is not picked up.
