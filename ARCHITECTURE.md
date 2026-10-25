# Architecture

Hot reload is split into two processes:

- **The app process** (a browser tab, or a Photino/PhotinoX desktop window) runs the `Dtr.Blazor.HotReload` runtime and connects to the server.
- **The server process** (`dtr-blazor-hotreload`) watches the app's source, compiles changes, and sends either in-place
  metadata updates or complete new generations over a WebSocket.

The normal build output is the starting point ("generation 0"). The server never mutates the app's `bin/` output; all hot
reload artifacts are compiled in memory (and, for the delta baseline, cached under the app's `obj/` directory).

## System overview

```mermaid
flowchart TB
    subgraph Dev["Dev machine"]
        direction TB

        subgraph AppProcess["App process — browser tab or Photino window"]
            direction TB
            Host["Blazor host<br/>WebAssemblyHostBuilder / PhotinoBlazorAppBuilder"]
            subgraph HotRuntime["Dtr.Blazor.HotReload runtime"]
                direction TB
                Root["HotReloadRoot<br/>root component + status overlay"]
                RuntimeCore["HotReloadRuntime<br/>generation lifecycle + swaps"]
                Swappable["SwappableServiceProvider<br/>forwards to the current generation"]
                State["StateCarryOver / StateConverter<br/>statics, services, components"]
                Client["HotReloadClient<br/>WebSocket + catch-up"]
            end
            Generation["Current AppGeneration<br/>DI container + async scope"]
            Assemblies["App + changed libraries<br/>loaded in GenerationLoadContext"]
        end

        subgraph ServerProcess["dtr-blazor-hotreload — 127.0.0.1:2009"]
            direction TB
            Hub["HotReloadHub<br/>generations, deltas, clients"]
            Worker["CompileWorker<br/>watchers + debounced loop<br/>builds scoped CSS"]
            Graph["ProjectGraph<br/>project references + compiler response files"]
            Solution["HotSolution<br/>Roslyn workspace + emit"]
            Deltas["DeltaSession<br/>Edit-and-Continue via reflection"]
            DevServer["DevServerProcess<br/>optional dotnet run"]
        end

        Files[("Source files<br/>bin/ and obj/")]
        SDK[("Installed .NET SDK<br/>MSBuild, Roslyn + dotnet-watch")]
        Targets["Dtr.Blazor.HotReload.targets<br/>Debug-only build integration"]
    end

    Host --> Root
    Root --> RuntimeCore
    RuntimeCore --> Swappable
    RuntimeCore --> Generation
    RuntimeCore --> State
    RuntimeCore --> Client
    Generation --> Assemblies

    Client <-->|"WebSocket /hotreload"| Hub
    Client -->|"HTTP GET /hotreload/{generation}/{file}"| Hub

    Worker -->|"watches"| Files
    Worker --> Graph
    Worker --> Solution
    Worker --> Deltas
    Worker --> Hub
    Worker --> DevServer
    Worker -->|"dotnet build (scoped CSS)"| SDK
    Graph -->|"dotnet msbuild -getProperty / -getItem"| Files
    Solution -->|"loads compiler from"| SDK
    Deltas -->|"loads hot reload service from"| SDK
    Targets -->|"dtr-hotreload.rsp / .txt"| Files
    DevServer -->|"serves or starts the app"| Host
```

On disk, the app and server only share the app's build output and the files the build targets leave in `obj/`. The app
does not reference the server assembly, and the server does not load the app assembly.

## Build-time integration

The app references one of the runtime packages only in Debug builds. The package's targets define `HOTRELOAD` and capture
the exact compiler command line of the app, so the server can reproduce the build with the same references, source
generators (including Razor), defines, and analyzer settings.

```mermaid
flowchart LR
    App["App project<br/>(Debug)"] -->|"PackageReference / ProjectReference"| Package["Dtr.Blazor.HotReload<br/>or .Desktop / .Desktop.PhotinoX"]
    Package -->|"imports build/Dtr.Blazor.HotReload.targets"| Targets["MSBuild targets"]
    Targets -->|"Debug only"| Define["DefineConstants += HOTRELOAD"]
    Targets -->|"ProvideCommandLineArgs=true"| Args["@(CscCommandLineArgs)"]
    Args --> Rsp["obj/.../dtr-hotreload.rsp"]
    Args --> Info["obj/.../dtr-hotreload.txt"]
    Define --> Program["Program.cs<br/>UseHotReload(ConfigureServices, ...)"]
    Rsp --> Location["Server: ProjectLocation + ProjectGraph"]
    Info --> Location
```

For the app, the server reads the response file from the last regular build. For referenced libraries it asks MSBuild for
their compiler command lines with a design-time query, so the libraries do not need any build changes. This is also why a
regular `dotnet build` is only required when the project graph, references, packages, or anything generated at build time
changes. Scoped CSS is generated at build time too, but the server handles that itself: a saved `.razor.css`/`.cshtml.css`
triggers a build, then a reset (see below).

## Server pipeline

`CompileWorker` is the single writer. File watchers and WebSocket messages only add to sets under a lock and signal the
loop; the loop takes a snapshot, handles one batch, and repeats. That keeps compiles serialized and lets changes that arrive
during a compile be handled next.

```mermaid
flowchart TB
    Save["Developer saves a file"] --> SourceWatch["FileSystemWatcher<br/>source directories"]
    Build["Regular dotnet build"] --> BuildWatch["FileSystemWatcher<br/>assembly outputs + dotnet.js<br/>(ignored during the server's own build)"]

    SourceWatch --> Debounce["Debounce<br/>100 ms<br/>(300 ms for scoped CSS)"]
    BuildWatch --> BuildDebounce["Build debounce<br/>2 s"]

    Debounce --> Loop["CompileWorker loop<br/>one batch at a time"]
    BuildDebounce --> Loop

    Loop -->|"regular build finished"| Restart["LoadProjectsAsync + Hub.Reset<br/>restart dev server or tell clients to reload"]
    Loop -->|"scoped CSS"| BuildApp["dotnet build app<br/>-c Debug --nologo --no-restore"]
    BuildApp -->|"success"| Restart
    BuildApp -->|"failure"| Errors
    Loop -->|"source change"| Sync["HotSolution.SyncAsync<br/>update Roslyn documents"]
    Sync --> TryDelta{"Try in-place?<br/>capabilities + no IServiceCollection + not forced"}
    TryDelta -->|"yes"| Delta["DeltaSession.GetUpdatesAsync"]
    TryDelta -->|"no"| Full["FullSwapAsync<br/>emit app + changed libraries"]
    Delta -->|"Ready"| Hub["HotReloadHub<br/>broadcast + catch-up state"]
    Delta -->|"rude edit / can't emit"| Full
    Delta -->|"no changes"| NoChanges["Broadcast nochanges"]
    Full -->|"success"| Hub
    Full -->|"compile errors"| Errors["Broadcast compiler errors<br/>previous version keeps running"]
    Hub --> Clients["HotReloadClient(s)"]
    NoChanges --> Clients
    Errors --> Clients
    Loop -->|"CSS change"| Css["Hub.PublishCss"]
    Loop -->|"other static asset"| Reload["Broadcast reload"]
    Css --> Clients
    Reload --> Clients
```

Important server-side details:

- **Project graph** — `ProjectGraph` finds the app's project references, orders them dependencies-first, and obtains each
  project's compiler response file. Projects that are part of the hot reload machinery are excluded.
- **Roslyn workspace** — `HotSolution` keeps source and Razor documents in sync with disk, loads Roslyn from the SDK (so it
  matches the SDK's Razor generator), and emits assemblies for either deltas or full swaps.
- **Scoped CSS** — scoped CSS is generated by the SDK at build time (the scope attributes and the bundled stylesheet), so the
  in-memory compiler can't apply it. A saved `.razor.css`/`.cshtml.css` is debounced, then the server runs
  `dotnet build <app.csproj> -c Debug --nologo --no-restore`, ignores the build-output watcher events it caused, and
  resets/restarts from the completed output itself.
- **Delta engine** — `DeltaSession` drives Roslyn's internal hot reload service through reflection. If its shape changes or
  the runtime does not support deltas, the server falls back to full swaps.
- **Full-swap set** — a full swap contains the app plus every library whose code differs from the build output (including
  in-place updates) and the libraries that depend on those. One dependency-ordered pass builds the set.
- **Baseline** — after a full swap, the exact emitted assemblies and PDBs are written to
  `obj/.../dtr-hotreload/{generation}/`, and become the baseline for the next in-place update. Generation 0 uses the app's
  regular build output (and each library's reference assembly or build output).
- **Hub** — `HotReloadHub` is the source of truth for the current generation and the ordered deltas on top of it. On every
  connect/reconnect it sends `hello` with the current generation, deltas, and changed CSS, so a reloaded page can catch up.
  It keeps a few recent generations so a reconnecting client can still download them.

## In-place update (delta)

In-place updates patch the running assemblies with `MetadataUpdater.ApplyUpdate`, then invoke the registered
`[MetadataUpdateHandler]` callbacks (Blazor re-renders all components). Nothing is recreated, so all state is kept.

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Developer
    participant FS as File system
    participant W as CompileWorker
    participant S as HotSolution
    participant D as DeltaSession
    participant H as HotReloadHub
    participant C as HotReloadClient
    participant R as HotReloadRuntime
    participant A as Running app

    Dev->>FS: Save .cs / .razor
    FS-->>W: FileSystemWatcher event
    W->>W: Debounce and snapshot changes
    W->>S: SyncAsync(changed projects)
    S-->>W: Roslyn documents updated
    W->>H: Broadcast "compiling"
    W->>D: GetUpdatesAsync(solution)
    D-->>W: Status.Ready + per-module updates
    W->>H: PublishDelta(updates)
    H-->>C: delta message (generation, sequence, updates)
    C->>R: ApplyDeltaAsync(updates)
    R->>A: MetadataUpdater.ApplyUpdate(...)
    R->>A: [MetadataUpdateHandler] callbacks
    A-->>R: Blazor re-renders all components
    R-->>C: Status "applied in place, state kept"
    Note over W,A: Constructors and field initializers of existing objects do not run again.
```

If the delta engine reports a rude edit, compile errors, or the change touches `IServiceCollection`, the request goes to
the full-swap path instead. A change touching service registrations would otherwise patch code without rebuilding the DI
container.

## Full swap (new generation)

A full swap compiles a fresh set of assemblies, loads them into a new `AssemblyLoadContext`, builds a new DI container from
the new app assembly, and re-renders the root. State that can be converted is carried over.

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Developer
    participant W as CompileWorker
    participant S as HotSolution
    participant H as HotReloadHub
    participant C as HotReloadClient
    participant R as HotReloadRuntime
    participant G as GenerationLoadContext
    participant New as New AppGeneration
    participant Old as Previous AppGeneration
    participant Root as HotReloadRoot

    Dev->>W: Save (rude edit / DI change / no delta support)
    W->>S: SyncAsync + EmitAsync(app + changed libraries)
    S-->>W: assemblies + portable PDBs
    W->>H: Publish(assemblies)
    H-->>C: update message (generation, assembly names)
    C->>H: GET /hotreload/{generation}/{name}.dll or .pdb
    H-->>C: assembly + PDB bytes
    C->>R: ApplyAsync(assemblies)
    R->>G: load new assembly set
    R->>New: BuildGeneration(number)
    New->>New: ConfigureServices + build DI
    R->>Old: capture statics, resolved services, components
    R->>New: CarryOverServicesAndStatics()
    R->>New: InitializeServices()
    R->>R: Current = new generation
    R->>R: update renderer cascading values
    R->>Root: RenderGenerationAsync()
    Root->>New: render keyed subtree
    New-->>Root: new components receive carried-over state
    R->>Old: DisposeAsync()
    R-->>C: Status "app restarted, state carried over"
```

If compilation fails, the hub publishes nothing; the server broadcasts `errors` and the client keeps the previous
generation running. If loading or building the new generation fails, the runtime keeps the previous generation active too.

## Runtime object model and DI tiers

There are two service tiers:

- **Host tier** — services registered on the host builder before `UseHotReload` (framework defaults, logging, Photino
  services). They live as long as the page/process.
- **App generation** — services registered by the app's static `ConfigureServices`, rebuilt for every full swap.

The host's resolved singletons/scoped services are forwarded into each generation as instances, so both tiers share the
same host objects and the generation container never disposes them. Transients and open generics are simply re-registered.

```mermaid
flowchart TB
    subgraph HostTier["Host tier — page / process lifetime"]
        HostSP["Host ServiceProvider<br/>builder services"]
        SwappableRoot["SwappableServiceProvider (root)"]
        Activator["HotReloadComponentActivator"]
        Runtime["HotReloadRuntime"]
        Client["HotReloadClient"]
    end

    subgraph GenTier["App generation — replaced on every full swap"]
        GenSP["Generation ServiceProvider<br/>+ AsyncServiceScope"]
        AppServices["App services<br/>ConfigureServices()"]
        Components["Component tree<br/>rendered by HotReloadRoot"]
    end

    HostSP --> SwappableRoot
    SwappableRoot -->|"service resolution"| GenSP
    GenSP --> AppServices
    AppServices --> Components
    HostSP -.->|"resolved singletons/scoped<br/>forwarded as instances"| GenSP
    Activator -->|"creates components, restores state"| Components
    Runtime -->|"owns and swaps"| GenSP
    Runtime --> Client
```

`SwappableServiceProvider` is what Blazor sees: the root instance is returned by the container factory, and the page scope
is created by the host. Both forward every resolution to the current `AppGeneration`, which is why a swap does not require
restarting the host.

Browser and desktop differ in who owns the page scope:

- **Browser** — the WebAssembly host builds the container through `HotReloadServiceProviderFactory`; the runtime keeps the
  host services and builds generation 0 immediately.
- **Photino / PhotinoX desktop** — the host owns the page scope. The runtime creates a fresh host scope when the WebView
  attaches/reloads, rebuilds generation 0 on top of it, and keeps the already-loaded assemblies (so in-place updates stay
  applied across a page reload).

## Client/server protocol

The runtime uses one WebSocket (`/hotreload`) plus a small HTTP endpoint for assembly bytes.

| Direction | Message | Purpose |
| --- | --- | --- |
| Server → client | `hello` | Current session, generation, assemblies, deltas, changed CSS (on connect/reconnect) |
| Server → client | `compiling` | Show progress in the overlay |
| Server → client | `nochanges` | The save produced no compiled change |
| Server → client | `update` | A new generation is available; download the listed assemblies |
| Server → client | `delta` | An in-place update for generation `G`, sequence `S` |
| Server → client | `errors` | Compiler diagnostics (or a failed scoped CSS build); keep running the previous version |
| Server → client | `css` | Re-fetch one changed stylesheet |
| Server → client | `reload` | Reload the page (static asset or dev-server restart) |
| Server → client | `rebuilt` | A regular build was detected without `--devserver`; the user must restart |
| Client → server | `capabilities` | Whether this runtime supports in-place updates, and which kinds |
| Client → server | `delta-failed` | The client could not apply a delta; request a full swap |
| Client → server | `restart` | Request a full swap (`⟳` fresh start or error recovery) |
| Client → server | `GET /hotreload/{generation}/{file}` | Download a generation's `.dll` or `.pdb` |

## Invariants and failure behavior

- The server compiles one batch at a time; a change that arrives during a compile is queued for the next iteration.
- A scoped CSS build is the server's own build: its output watcher events are suppressed, and the server resets from the
  completed output instead of reacting to it as a regular build.
- The (generation, delta sequence) pair is monotonic per session. A client that falls behind catches up from `hello`, or
  requests a full swap if it detects a gap.
- A full swap never replaces the current generation until the new one has loaded, its services have been built, and state
  has been captured. Failures before the swap leave the running app untouched.
- The old `AssemblyLoadContext` cannot be unloaded. The browser reloads the page after `HotReload:MaxGenerations` swaps;
  the desktop keeps the process across page reloads until it is restarted.
- State carry-over is best effort. Fields whose type changed incompatibly, delegates, tasks, JS object references, and
  component references are left for the new code to recreate.
