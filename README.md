# Dtr.Blazor.HotReload

Hot reload for Blazor WebAssembly apps that applies (almost) any change within ~1-2 seconds, without rebuilding, restarting,
or reloading the page.

Two packages:

| Package | What |
| --- | --- |
| `Dtr.Blazor.HotReload` | The browser side: a library for the app, plus build targets. Reference it in Debug builds only. |
| `Dtr.Blazor.HotReload.Server` | The hot reload server, as a .NET tool (`dtr-blazor-hotreload`). Watches and compiles the app, and talks to the browser. |

Requires .NET 10 (SDK and app).

## Server

Install the server as a (local) tool, and run it next to the app's dev server:

```sh
dotnet new tool-manifest   # if there isn't one yet
dotnet tool install Dtr.Blazor.HotReload.Server
dotnet build path/to/App.csproj -c Debug
dotnet tool run dtr-blazor-hotreload --project path/to/App.csproj
```

Server options:

| Option | Default |
| --- | --- |
| `--project <path>` | The only `.csproj` in the current directory |
| `--port <port>` | `2009` |

### How it works

- Keeps a Roslyn workspace with the app. It reuses the exact compiler command line of the last regular build (captured by the package's build targets):
  the same references, the same source generators (including Razor), the same editorconfigs, and so on.
- Roslyn and the workspace layer are loaded from the installed SDK (its Roslyn and dotnet-watch), so they always match the SDK's Razor generator.
- On every save it recompiles the app (incrementally: only changed documents are re-parsed), emits it to memory, and tells the browser through a WebSocket.
  A reloaded page catches up with the latest generation.

### Files it leaves behind

- `dtr-hotreload.rsp` / `dtr-hotreload.txt` in the app's intermediate (obj) directory: its compiler command line.

## Building the packages

```sh
dotnet test Dtr.Blazor.HotReload.slnx
dotnet pack Dtr.Blazor.HotReload.slnx -c Release    # -> artifacts/*.nupkg
```

Bump `VersionPrefix` in `Directory.Build.props` for every release: NuGet caches packages by version, so a repacked package with the same version is not picked up.
