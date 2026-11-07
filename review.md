# Code review: whole repository

Reviewed 2026-11-07. This covers the whole repository (about 8,100 lines), not only one branch. Three reviewers split the work by area:

- the client runtime (`src/BlazorHMR`)
- the dev server (`src/BlazorHMR.Server`)
- the protocol, the Photino/PhotinoX hosts, the tests, and the build/CI setup

**Status:** all projects build, and the 19 unit tests pass locally in Debug. The only warnings are BL0006. CI never runs the tests (see 8).

**How to read the markers:**

- ✔ means the finding was checked directly against the code during the review.
- "Likely" means the failure depends on timing or framework behaviour that nobody reproduced.
- Everything else was traced through the code by a reviewer.

## Highest impact

1. ✔ **The `BlazorHMR=false` opt-out does nothing.** `src/BlazorHMR/build/BlazorHMR.targets:7`
   - **Cause:** the condition `'$(BlazorHMR).ToUpper()' != 'FALSE'` is not a property function. MSBuild expands it to the literal text `false.ToUpper()`, so the condition is always true. There is also no Debug-only guard.
   - **Effect:** a consumer's Release build still captures the compiler arguments and writes `blazor-hmr.rsp` and `blazor-hmr.txt`. Evaluating with `-p:BlazorHMR=false -p:Configuration=Release` gives `ProvideCommandLineArgs=true`.
   - **Fix:** `'$(BlazorHMR)' != 'false'` (MSBuild compares case-insensitively).

2. ✔ **Photino and PhotinoX apps never get the targets file.** `src/BlazorHMR/BlazorHMR.csproj:30`
   - **Cause:** `BlazorHMR.targets` is packed only under `build/`. The desktop packages depend on BlazorHMR with `exclude="Build,Analyzers"`.
   - **Effect:** an app that references only `BlazorHMR.Photino(X)` never writes `blazor-hmr.txt`. The server then fails with "still not found" (`ProjectLocation.cs:21-36`).
   - **Fix:** also pack the targets file under `buildTransitive/`.

3. ✔ **PhotinoX checks `initializeServices` and then ignores it.** `src/BlazorHMR.PhotinoX/HotReloadExtensions.cs:41,73-82`
   - **Defect:** the parameter is validated but never passed to `HotReloadRuntime`.
   - **Effect:** an app that relies on it has uninitialized static state after the first full swap, and nothing reports this.
   - **Docs drift in both hosts:**
     - Photino's docs describe `configuration` and `initializeServices`, but Photino has neither parameter.
     - Both hosts say the signature must be `(IServiceCollection, IConfiguration)`, but the code requires only `(IServiceCollection)`.

4. **Shared disposables are disposed after a full swap.** `src/BlazorHMR/FullSwap/FullSwapApplier.cs:41,56` and `StateConverter.cs:110-114`
   - **Cause:** `CopyFields` copies framework-typed fields into the new service by reference. Examples: `HttpClient`, `SemaphoreSlim`, `Stream`, `HubConnection`, `IDisposable` subscriptions. It then disposes the old service, which disposes those shared objects.
   - **Effect:** the new service throws `ObjectDisposedException`, and the resources its constructor created leak.

5. **Inheriting from a framework generic breaks carry-over.** `src/BlazorHMR/FullSwap/StateConverter.cs:156-180`
   - **Example:** a class such as `class TodoList : List<TodoItem>` makes `CopyFields` throw `ArgumentException`.
   - **In a component field:** the page shows the error boundary after the swap.
   - **In a service field:** the exception is caught at `FullSwapApplier.cs:43`, and every remaining service and all statics are silently skipped.

6. **The project's own source list is ignored.** `src/BlazorHMR.Server/Project/ProjectInputs.cs:81-108`
   - **Cause:** the server takes every `*.cs` file under the project folder and ignores the project's compile includes and removes. `IsExcludedDirectory` also checks only the first path segment.
   - **Effect:** when one project's folder contains another project, the outer project gets duplicate types and attributes (CS0101/CS0579) on its first edit, so every compile fails. Edits in the inner project also mark the outer project as changed.

## Silent loss of edits or stale code

7. **On desktop, a server restart leaves the client on old code.** `src/BlazorHMR/HotReloadClient.cs:183-187` and `PatchSequence.cs:32`
   - **Cause:** `StartSession` replaces only `Session` and keeps `Generation` and `AppliedPatches`. The desktop page reload keeps the connection open, so no new hello arrives.
   - **Effect:** the page stays on the old code. The new session's first patches can also be skipped silently as "already applied".

8. ✔ **CI does not run tests and has publishing problems.** `build.sh:24` and `.github/workflows/ci.yml`
   - The `dotnet test` line is commented out, and it points at another repository's project (`./src/DeclarativeCommandLine.UnitTest`).
   - The trigger `branches: ["*"]` does not match branch names that contain `/`, so a branch such as `review/15-refactor` gets no CI. There is also no `pull_request` trigger.
   - Every other branch publishes to nuget.org. Re-running a job fails with a 409 because `--skip-duplicate` is not set.

9. **On desktop, hitting the `MaxGenerations` limit drops the change that caused it.** `src/BlazorHMR/HotReloadClient.cs:268-272`
   - **Effect:** the reload rebuilds the old assemblies, so the latest save is not applied. The same happens for a fresh-start generation (line 109).
   - **Also:** on desktop the old load contexts are not collectible, so the limit frees no memory. The `HotReloadOptions.MaxGenerations` doc ("in the browser") is wrong for these hosts.

10. **A failed scoped-CSS build drops the whole batch of changes.** `src/BlazorHMR.Server/Compilation/CompileWorker.cs:61,70-74`
    - **Cause:** `_changes.Take()` clears the batch before the build runs, and nothing is put back if it fails.
    - **Effect:** the `.razor.css` change is lost. So are C# edits to other projects that were queued in the same batch.

11. **A watcher buffer overflow is only logged.** `src/BlazorHMR.Server/Compilation/SourceWatcher.cs:38`
    - **Effect:** after a `git checkout` or a mass find-and-replace, the affected projects are never synced again. The server keeps compiling stale code until each project is edited.

12. **New projects are never watched.** `src/BlazorHMR.Server/Compilation/CompileWorker.cs:55`
    - **Cause:** `_watcher.Start` runs only once, at startup.
    - **Effect:** a project added as a reference later joins the solution, but its files are not watched until the server restarts.

13. **Likely: an edit saved while a build is in progress may never be applied.** `src/BlazorHMR.Server/Project/HotSolution.cs:76,224-232`
    - **Cause:** the edit-and-continue baseline is built from what is on disk at load time, not from what was actually compiled.
    - **Effect:** if a file is saved during the build delay, the workspace already contains the edit and reports no change against the running build.

14. **Likely: a short burst of source events cuts the build delay short.** `src/BlazorHMR.Server/Compilation/SourceWatcher.cs:111-117`
    - **Cause:** the source debounce (100 ms) or scoped-CSS debounce (300 ms) replaces a pending 2 s `BuildDebounce`.
    - **Effect:** the app can restart while `dotnet build` is still copying output.

## Robustness

15. **A malformed message drops the connection on both sides.** `src/BlazorHMR.Server/Server/HotReloadHub.cs:207` and `src/BlazorHMR/HotReloadConnection.cs:162`
    - **Cause:** when `type` is missing or is not the first property, System.Text.Json throws `NotSupportedException`. Both sides catch only `JsonException`.
    - **Effect:** the connection drops, although the code comments say a bad message should not take it down.

16. **An exception during a swap leaks the new generation.** `src/BlazorHMR/FullSwap/FullSwapApplier.cs:31-46`
    - **Cause:** before the commit point, only `InvalidOperationException` is handled. Any other exception (for example a `NullReferenceException` from an edited constructor) means `next` is never disposed.
    - **Effect:** the new generation's provider, scope and services keep running.

17. **Disposal stops at the first exception.** `src/BlazorHMR/HostTier.cs:92-98`, `FullSwap/AppGeneration.cs:71-72` and `FullSwapApplier.cs:56`
    - **Page reload:** when an app service throws while disposing, the root provider and host scope are never disposed.
    - **Full swap:** the client wrongly reports that the previous version is still running. Every later patch then looks out of sync and forces another full swap.

18. **Dictionaries lose their custom comparer when carried over.** `src/BlazorHMR/FullSwap/StateConverter.cs:266`
    - **Cause:** collections are rebuilt with `Activator.CreateInstance`.
    - **Effect:** an `OrdinalIgnoreCase` dictionary becomes case-sensitive after a swap, so lookups that used to succeed now miss.

19. **Saved component state is never cleared after the swap render.** `src/BlazorHMR/FullSwap/FullSwapApplier.cs:50` and `Blazor/HotReloadComponentActivator.cs:29`
    - **Effect:** a component created later can pick up stale fields from an old instance. The previous generation also stays in memory until the next swap.

20. **Likely: a client that connects during a broadcast can get the same generation twice.** `src/BlazorHMR.Server/Server/HotReloadHub.cs:54-62`
    - **Cause:** `Broadcast` runs after the lock is released.
    - **Effect:** the client does a duplicate full swap and loses component state.

21. **Likely: a failed restart after a scoped-CSS build leaves the app dead.** `src/BlazorHMR.Server/Compilation/BuildRunner.cs:24-53`
    - **Effect:** clients stay stuck on "Compiling…".

22. **The WebSocket endpoint has no Origin check and no frame size limit.** `src/BlazorHMR.Server/Server/HotReloadServer.cs:38-55` and `HotReloadHub.cs:161-176`
    - **Effect:** any web page you visit can connect to it, read patches, trigger restarts, or exhaust memory with one endless frame.
    - **Already tracked** in refactor.md 2.4.

23. **Likely: a client that stops reading can block server shutdown.** `src/BlazorHMR.Server/Server/ClientHub.cs:84-94`
    - **Cause:** `SendAsync` is called with `CancellationToken.None`.
    - **Effect:** server shutdown can hang, and the client's outgoing queue grows without limit.
    - **Already tracked** in refactor.md 2.5.

## Lower priority

- **Blazor internals fail silently when they change.** `src/BlazorHMR/FullSwap/StateCarryOver.cs:85,160-172`
  - Some reflection lookups use `?.` or `as`. If a private member is renamed, they return nothing and state is dropped without a log entry.
  - Other lookups use `!`. If a private member is renamed, they throw, and every full swap fails.
- **Null fields are not validated.** Protocol message fields are declared non-nullable, but a message from a different version can still deserialize them as null. That gives a `NullReferenceException` in the handlers (`ServerMessages.cs:31-38,74-79`).
- **Photino and PhotinoX differ.** Photino captures the host registrations inside `UseHotReload`, while PhotinoX captures them when the provider is built. Services registered after `UseHotReload()` therefore reach app generations on PhotinoX but not on Photino.
- **Prerelease versions sort by commit hash.** `build.sh:11` uses `pre-<sha>`, so the "latest prerelease" can be an older commit. A run number or timestamp would fix this.
- **Renovate cannot fully work.** `.github/workflows/renovate.yml:51` uses `GITHUB_TOKEN`, so its PRs never get CI and it cannot update workflow files. There is also no `renovate.json`.
- **Tests miss key areas.**
  - The protocol tests list message types by hand, so a new type missing from `[JsonDerivedType]` would not be caught.
  - Nothing tests `PatchSequence`, the hello and replay logic, or the hub's handling of bad messages.
  - Nothing tests the server at all.
- **Package and script cleanup:**
  - `Components.WebView` is pinned but no project uses it.
  - Desktop apps pull in the WebAssembly package.
  - `$CONF` is unquoted in `build.sh`, and the script does not use `set -u`.

## Earlier diff review (`review/15-refactor`)

- **Removed suppressions:** the `[SuppressMessage]` attributes for BL0006 and S3011 were removed from `HotReloadRoot`, and those warnings are back.
- **`SwappableServiceProvider` docs are wrong:**
  - The comment at line 42 says "return ourselves" for a new scope, but that branch returns the scope factory.
  - The `_isRoot` doc is wrong.
  - The `CreateScope` doc says it is "only reachable through the root", but a non-root branch exists.
- **`SwappableServiceProvider` cleanups:**
  - `GetRequiredService` looks a service up twice when it is missing.
  - The primary constructor's parameters are also copied into fields.
  - On the root instance, the synchronous `Dispose()` does nothing.
- **`HotReloadServiceProviderFactory` lost its docs:** `<inheritdoc/>` replaced the project-specific notes on the generation-0 side effect.
