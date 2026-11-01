# BlazorHMR Refactor Plan

Follow-up work after splitting the runtime into two explicit pipelines (full swap and in-place patch) and extracting the WebSocket transport into `HotReloadConnection`.

Guiding constraints:

- Keep the two pipelines explicit; do not merge them behind one polymorphic "apply patch" abstraction.
- Keep desktop support (Photino and PhotinoX) working.
- Do not create git commits; leave the tree uncommitted unless asked.
- Every phase must leave `dotnet build BlazorHMR.slnx` green and `dotnet test BlazorHMR.slnx` passing.

---

## Phase 1 — Correctness and concurrency

### 1.1 Serialize client sends

**Why:** `ClientWebSocket` allows one send at a time. `HotReloadConnection.SendAsync` is called from the receive loop (`patch-failed`) and from the UI (`RequestRestartAsync`, fresh-start button), so concurrent sends can throw.

**Files:** `src/BlazorHMR/HotReloadConnection.cs`

**Steps:**

- [ ] Add a `Channel<byte[]>` outbox and a single send loop, mirroring `ClientHub.Client` on the server.
- [ ] `SendAsync` only serializes and writes to the channel; it never touches the socket directly.
- [ ] Complete the writer and await the send loop in `DisposeAsync`.
- [ ] Handle `WebSocketException` in the send loop without killing the receive loop.

**Acceptance:** two concurrent `SendAsync` calls never throw; shutdown drains the outbox and exits cleanly.

### 1.2 Isolate message-handler failures from the connection

**Why:** `_onMessage` runs inside the same `try` as the socket loop in `RunAsync`; any exception from `HotReloadClient.HandleAsync` is logged as "Server not reachable" and forces a reconnect.

**Files:** `src/BlazorHMR/HotReloadConnection.cs`

**Steps:**

- [ ] Wrap the per-message dispatch in its own `try/catch`.
- [ ] Log handler failures distinctly from transport failures.
- [ ] Keep the socket open after a handler failure.

**Acceptance:** a handler that throws logs a handler error and the next message is still received on the same connection.

### 1.3 Validate session and patch continuity

**Why:** `OnPatchAsync` checks generation/sequence but not `message.Session`. `OnHelloAsync` replays patches with only `patch.Sequence > AppliedPatches`, so a hello can apply a gap out of order.

**Files:** `src/BlazorHMR/HotReloadClient.cs`, `src/BlazorHMR/PatchSequence.cs`

**Steps:**

- [ ] Reject patches whose `Session` does not match `PatchSequence.Session`.
- [ ] Use `IsAlreadyApplied` / `IsNextPatch` in the hello replay loop too.
- [ ] On a gap in either path, request a full swap (`patch-failed`) instead of silently stopping.

**Acceptance:** unit tests cover already-applied, next, and gap cases for both live and hello replay paths.

### 1.4 Make sends observable

**Why:** `SendAsync` silently drops when the socket is closed. `RequestFreshStartAsync` sets `_freshStartRequested = true` before sending; if the send is dropped, the next unrelated generation reloads the page.

**Files:** `src/BlazorHMR/HotReloadConnection.cs`, `src/BlazorHMR/HotReloadClient.cs`

**Steps:**

- [ ] Return `bool` (or a result) from `SendAsync`.
- [ ] Clear `_freshStartRequested` when the restart request was not sent.
- [ ] Make the fire-and-forget restart in `HotReloadRuntime.OnUnhandledException` observe failures.

**Acceptance:** a dropped send leaves the client in a consistent state and logs the failure.

### 1.5 Make `Start` idempotent and close gracefully

**Files:** `src/BlazorHMR/HotReloadConnection.cs`

**Steps:**

- [ ] `Start()` uses `_loop ??= RunAsync(...)`.
- [ ] `DisposeAsync` attempts `CloseOutputAsync` before cancelling.

**Acceptance:** calling `Start()` twice starts one loop.

### 1.6 Roll back a failed full swap after the commit point

**Why:** `FullSwapApplier` sets the new generation, then renders, then disposes the old one. A render failure leaves the runtime on the new generation with the old tree, and leaks the old generation.

**Files:** `src/BlazorHMR/FullSwap/FullSwapApplier.cs`, `src/BlazorHMR/HotReloadRuntime.cs`

**Steps:**

- [ ] Wrap the render/dispose section in `try/catch`.
- [ ] On failure, either restore `previous` as current and dispose `next`, or document and handle the inconsistent state explicitly.
- [ ] Always dispose `next` when the swap is abandoned.

**Acceptance:** a throwing render leaves the previous generation active and does not leak `next`.

---

## Phase 2 — Robustness and operations

### 2.1 Status subscriber isolation

**Files:** `src/BlazorHMR/HotReloadRuntime.cs`

- [ ] Invoke each `StatusChanged` subscriber in its own `try/catch`.
- [ ] Log subscriber failures instead of breaking the patch pipeline.

### 2.2 Configurable options

**Files:** `src/BlazorHMR/HotReloadOptions.cs`, `src/BlazorHMR/HotReloadRuntime.cs`, host extensions

- [ ] Read `BLAZOR_HMR_URL` and `BLAZOR_HMR_MAX_GENERATIONS` from the environment.
- [ ] Allow an options object to be supplied by the host.
- [ ] Cache the `ServerUrl` `Uri`.
- [ ] Update the `MaxGenerations` comment (it still mentions the browser).

### 2.3 Logging abstraction

**Files:** `src/BlazorHMR/*.cs`, optionally `src/BlazorHMR.Server/Log.cs`

- [ ] Introduce a small client-side logger (or use `Microsoft.Extensions.Logging` via DI).
- [ ] Replace `Console.WriteLine` in the client with leveled logging.
- [ ] Let hosts route/silence output; make tests quiet.

### 2.4 Server hardening

**Files:** `src/BlazorHMR.Server/Server/HotReloadServer.cs`

- [ ] Add an `Origin` allowlist (or a startup token) for `/hotreload`.
- [ ] Add `Cache-Control: no-store` to generation downloads.
- [ ] Validate that a successful `CompileResult` has both `Assembly` and `Symbols` before storing it in `GenerationStore`.

### 2.5 Bound the server client outbox

**Files:** `src/BlazorHMR.Server/Server/ClientHub.cs`

- [ ] Use a bounded channel with drop-oldest plus a warning, or disconnect a client that falls too far behind.
- [ ] Link a shutdown token into the send loop.
- [ ] Catch non-`WebSocketException` failures in the send loop.

### 2.6 Cancellable patch gate

**Files:** `src/BlazorHMR/PatchGate.cs`, `src/BlazorHMR/HotReloadRuntime.cs`

- [ ] Add `EnterAsync(CancellationToken)`.
- [ ] Do not dispose the semaphore while a lease is held (drain on shutdown).
- [ ] Consider an `IAsyncDisposable` lease.

---

## Phase 3 — Architecture

### 3.1 Shared protocol contract — done

**Why:** message names and payload shapes are duplicated as string literals and anonymous objects across client and server.

**Files:** new `src/BlazorHMR.Protocol/`, `HotReloadClient.cs`, `HotReloadConnection.cs`, `HotReloadHub.cs`, `CompileWorker.cs`

- [x] Add `MessageTypes` constants for every wire message.
- [x] Add typed request/response records for both directions.
- [x] Replace anonymous objects in `HotReloadHub` / `CompileWorker` and `HotReloadClient` sends.
- [x] Use `[JsonPolymorphic]` for `ServerMessage` and `ClientMessage`, so each message type has exactly its fields.
- [x] Include a protocol version in hello/capabilities and log a mismatch.
- [x] Share `CompileDiagnostic`, `PatchChange`, and the `JsonSerializerOptions` (`ProtocolJson`).
- [x] Add `BlazorHMR.Protocol` to the solution, the client/server/test project references, and `pub.sh`.

**Notes:** the protocol project is packable (`BlazorHMR.Protocol`), because the client package depends on it. The server tool embeds the DLL, so it needs no package dependency. Messages must be serialized as the base type (`ServerMessage` / `ClientMessage`) for the `type` discriminator to be written.

**Acceptance:** no protocol string literals remain outside the contract type; client and server compile against the same types. Verified by `ProtocolMessageTest` (round-trips and discriminator names).

### 3.2 Extract host-tier and generation building from `HotReloadRuntime` — done

**Files:** `src/BlazorHMR/HotReloadRuntime.cs`, new `src/BlazorHMR/FullSwap/GenerationFactory.cs`, new `src/BlazorHMR/HostTier.cs`

- [x] Move `BuildGeneration`, `ForwardHostServices`, `InvokeConfigureServices`, `Invoke` into `GenerationFactory`.
- [x] Move `InitializeDesktop`, `InitializeHost`, `CreateHostScope`, `DisposeGenerationAsync` into `HostTier`. `CreateHostScope` stays on the runtime as the coordinator: it asks the host tier for a new scope (`ReplaceScope`/`RestoreScope`) and rebuilds the current generation.
- [x] Keep `HotReloadRuntime` as the coordinator over host tier, current generation, and the two pipelines.
- [x] Delete the commented-out `Initialize`, `ManagesHostScope`, and `InvokeInitializeServices` blocks.
- [x] Precompute host descriptor registration indices (remove the O(n²) `IndexOf` in `ForwardHostServices`).

**Notes:** `HostTier` owns the host descriptors, host provider, and page scope. `GenerationFactory` takes the entry point and the host tier; it caches the per-descriptor registration index (an `int[]` parallel to the descriptor list) and rebuilds it only when the host descriptor list changes. `HotReloadRuntime` dropped from ~490 to ~280 lines.

**Acceptance:** `HotReloadRuntime` shrinks substantially; desktop page reload and full swap still behave the same. Covered by `GenerationFactoryTest` (host services are forwarded as the same instances, in registration order) plus the existing suite.

### 3.3 Split `CompileWorker` — done

**Files:** `src/BlazorHMR.Server/Compilation/CompileWorker.cs`, new `SourceWatcher.cs`, `ChangeClassifier.cs`, `BuildRunner.cs`

- [x] Move `IsIgnored`, `IsScopedCss`, `OnSourceChanged` into `ChangeClassifier`.
- [x] Move watcher construction/`StartWatching` into `SourceWatcher`.
- [x] Move `OnBuildAsync`, `BuildScopedCssAsync`, and the dotnet CLI invocation into `BuildRunner`.
- [x] Keep `CompileWorker` as the single loop that drains change sets and calls the compilers.

**Notes:** `ChangeClassifier` owns the change sets, the lock, debounce constants, and the build-output grace period; it returns a `ChangeSet` snapshot per loop iteration. `SourceWatcher` owns the `FileSystemWatcher`s and the debounce timer, and only reports "something changed" to the loop. `BuildRunner` owns the real builds (regular build restart and scoped CSS) plus the dotnet CLI call. `CompileWorker` went from ~580 to ~280 lines and reads as a dispatcher.

**Acceptance:** the worker loop reads as a short dispatcher; behavior unchanged. Verified by the existing suite (19/19) and a clean build.

### 3.4 Explicit in-place result

**Files:** `src/BlazorHMR.Server/Compilation/InPlacePatchCompiler.cs`, `CompileWorker.cs`

- [ ] Replace the `PatchStatus?` tri-state with an explicit enum (`Unavailable`, `NoChanges`, `Ready`, `CompileErrors`, `RestartRequired`) or a discriminated result.
- [ ] Remove the `default:` branch that silently means "unavailable".

### 3.5 Full-swap request object

**Files:** `src/BlazorHMR.Server/Compilation/FullSwapCompiler.cs`, `CompileWorker.cs`

- [ ] Replace the five-parameter method (including the `Func<Task>` baseline callback and `bool storeBaseline`) with a request record plus a small interface for restarting the in-place baseline.

### 3.6 Encapsulate the hub gate

**Files:** `src/BlazorHMR.Server/Server/HotReloadHub.cs`, `GenerationStore.cs`, `PatchHistory.cs`, `CssChangeTracker.cs`, `ClientHub.cs`

- [ ] Stop passing `Lock gate` into each store; let the hub own the lock and expose intent methods, or introduce a `HubState` object that owns the gate.
- [ ] Document the invariant that a hello sees generation, patches, and CSS from one moment.
- [ ] Store a typed patch message in `PatchHistory` instead of `object`.

---

## Phase 4 — Tests, naming, cleanup

### 4.1 Unit tests for the extracted pure logic

**Files:** `test/BlazorHMR.UnitTest/`

- [ ] `PatchSequenceTest`: new session, applied generation, already applied, next, gap.
- [ ] `PatchPlannerTest`: full-swap request, unsupported runtime, service-registration file, unrelated file.
- [ ] `GenerationStoreTest`: retention and eviction.
- [ ] `PatchHistoryTest`: add, snapshot, clear.
- [ ] `CssChangeTrackerTest`.
- [ ] `PatchGateTest`: serialization and cancellation.

### 4.2 Client transport test with a fake server

**Files:** new `test/BlazorHMR.UnitTest/HotReloadClientTest.cs` (or a separate integration test project)

- [ ] Reconnect, hello replay, missed patch resync.
- [ ] Dropped send on fresh start.
- [ ] Handler exception does not drop the connection.

### 4.3 Naming consistency — done

- [x] Rename `DeltaApplier` -> `PatchApplier`.
- [x] Rename `DeltaPayload` -> `PatchChange` (done as part of 3.1, since it is the wire type).
- [x] Rename server `DeltaSession`/`DeltaUpdate`/`DeltaStatus`/`DeltaResult` -> `PatchSession`/`PatchUpdate`/`PatchStatus`/`PatchResult`.
- [x] Keep "delta"/Edit-and-Continue wording only in prose and doc comments. The only remaining `Delta` identifiers are Roslyn's own field names (`MetadataDelta`, `ILDelta`, `PdbDelta`).

### 4.4 Warning and dead-code cleanup

- [ ] Fix the four CS1572 XML-comment warnings in `src/BlazorHMR.Photino/HotReloadExtensions.cs` and `src/BlazorHMR.PhotinoX/HotReloadExtensions.cs`.
- [ ] Remove the remaining commented-out blocks (CORS, `dotnet.js` watcher, etc.).
- [ ] Cache reflection metadata in `RenderCoordinator`, `AppGeneration`, and `HotReloadRoot`.
- [ ] Make `PageReloadCoordinator.GetModuleAsync` thread-safe if CSS and page reload can race.

### 4.5 Extract the overlay — done

**Files:** `src/BlazorHMR/Blazor/HotReloadRoot.cs`, new `HotReloadOverlay.cs`

- [x] Move `BuildOverlay`, `BuildFreshStartButton`, and the error content into an overlay component.
- [x] Add `role="status"` / `aria-live` for the status toast (and `role="alert"` for the error panel).
- [x] Keep `HotReloadRoot` focused on swapping the generation.

**Notes:** one `HotReloadOverlay` component serves both modes: status/fresh-start mode (no `Exception` parameter, subscribes to `Runtime.StatusChanged`) and error-panel mode (`Exception` set, rendered by the `ErrorBoundary`'s `ErrorContent`). The overlay is a sibling of the keyed error boundary, so it survives a generation swap and keeps its status subscription.

---

## Suggested order

1. Phase 1 (1.1–1.6): correctness and concurrency; smallest diff, highest risk reduction.
2. Phase 4.1: add tests for `PatchSequence` and `PatchPlanner` while the behavior is fresh.
3. Phase 2 (2.1–2.6): robustness.
4. Phase 3.1 (protocol contract): biggest maintainability win, but touches both sides; do it before 3.2–3.6.
5. Phase 3.4–3.6, then Phase 4.2–4.4 (3.1–3.3, 4.3, and 4.5 are done).

## Verification per phase

- `dotnet build BlazorHMR.slnx`
- `dotnet test BlazorHMR.slnx`
- Manual check against a Photino/PhotinoX sample: patch in place, full swap, fresh start, page reload, CSS reload.
