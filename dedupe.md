# Cutting down lines of code

Rough inventory (C# only):

- 7,254 total lines
- 4,824 code lines
- 1,494 doc-comment lines
- 936 blank lines

The codebase is already well-decomposed (93 files, many under 50 lines), so there is no big pile of dead
code to delete. The wins are duplication, boilerplate, and hand-written render code.

## Highest-value, lowest-risk

| # | Change | Where | Rough LOC saved | Risk |
|---|---|---|---|---|
| 1 | Deduplicate the two desktop host extensions | `src/BlazorHMR.Photino/HotReloadExtensions.cs`, `src/BlazorHMR.PhotinoX/HotReloadExtensions.cs` | ~80–100 | Low |
| 2 | Rewrite the overlay as a `.razor` component | `src/BlazorHMR/Blazor/HotReloadOverlay.cs` | ~60–80 | Low |
| 3 | Consolidate the four hub stores | `Server/{GenerationStore,PatchHistory,CssChangeTracker,ClientHub}.cs` | ~80–120 | Medium |
| 4 | Add a status factory/overload | 11 `new HotReloadStatus(...)` sites | ~20–30 | Low |
| 5 | Merge the two in-place result records | `InPlace/PatchResult.cs`, `Compilation/InPlacePatchCompiler.cs` | ~20–30 | Low |
| 6 | Parameterize `SyncDocumentsAsync` | `Server/Project/HotSolution.cs` | ~20–30 | Medium |
| 7 | Unify app/library construction | `Server/Project/ProjectGraph.cs` | ~15–20 | Low |
| 8 | Share field-copy logic in state carry-over | `FullSwap/{StateCarryOver,StateConverter}.cs` | ~20–30 | Medium |
| 9 | Mechanical cleanup (dead comments, redundant usings, global usings) | several | ~50–70 | Very low |

Realistic total from 1–9: **~350–450 lines (~7–9% of code)**, mostly by removing duplication rather than
features.

## The top four in a bit more detail

### 1. Desktop host extensions

The two files are 284 lines and share 80 identical non-blank lines: static-method validation, runtime
construction, `AddSingleton(runtime)`, and `AddSingleton<IComponentActivator>`. Extract those into a helper
in `BlazorHMR` (both host projects already reference it), and leave each host file with only its
root-component replacement and container wiring. This also fixes the stale XML docs (the
`configuration`/`initializeServices` params that don't exist, noted as CS1572 in `refactor.md`).

### 2. Overlay as `.razor`

`HotReloadOverlay.cs` is 238 lines, of which roughly 125–140 are `OpenElement`/`AddAttribute`/`CloseElement`
calls with hand-maintained sequence numbers. The project is already `Microsoft.NET.Sdk.Razor`, so a `.razor`
file with the same markup and a small `@code` block would be shorter and much harder to break. Keep
`HotReloadRoot` as-is — it needs `Attach`/`SetParametersAsync` and the renderer handle.

### 3. Hub stores

`GenerationStore` (71), `PatchHistory` (51), `CssChangeTracker` (34), and `ClientHub` (98) all take the same
`Lock gate` and wrap every operation in it. Either fold them into one `HubState` that owns the gate and the
collections (the `refactor.md` 3.6 direction), or do the cheaper version: `PatchHistory` and
`CssChangeTracker` are structurally identical (`Add`/`Snapshot`/`Clear`) and can share a generic
`LockedList<T>`/`LockedSet<T>`. This is concurrency-sensitive, so it needs tests.

### 4. Status construction

Every pipeline writes `new HotReloadStatus(Kind, "message", [])`, often across 3–4 lines. Add
`HotReloadStatus.Applied(...)`, `.Compiling(...)`, `.Failed(...)`, or a `SetStatus(kind, message, details = [])`
overload. Small, but it touches the client, runtime, and both appliers.

## If raw LOC is the real goal: docs

There are **327 `<summary>` blocks totaling 1,232 lines** — 188 are exactly three lines (`<summary>`, text,
`</summary>`) and 101 are four lines. One-lining/condensing those could save **~400–500 lines without deleting
information**. But `GenerateDocumentationFile` is on and this is a shipped package, so only do this for
private/internal members, and only if line count is actually the metric.

## Smaller cleanups

- Remove the 15 redundant `using System;` lines (already global).
- Add `global using System.Reflection;` / `System.Diagnostics;` / `Microsoft.Extensions.DependencyInjection;`
  per project — saves ~50–60 lines but makes every file import them.
- Delete the commented-out CORS blocks in `HotReloadServer.cs`, `DevServerProcess.Url`, and the
  `--devserver-url` block in `Program.cs`.
- `StateCarryOver.Restore` has two `<summary>` blocks stacked (a copy-paste bug).
- `SourceWatcher.Start` repeats watcher construction twice; a small `Watch(...)` helper saves ~10–15 lines.
- `StateConverter.CopyFields` and `StateCarryOver.CarryOverStatics` duplicate the “find matching field, skip
  literals/readonly, convert, set” loop; one `TryCopyField` helper removes that.
- Tests: `StateConverterTest` (214), `ProtocolMessageTest` (165), `SectionSubscriptionsTest` (142) could use
  data-driven cases/helpers for another ~50–80 lines, though test LOC usually isn't worth optimizing.

## What I'd avoid

- **Splitting more files.** The project already has many small one-type files; further splitting increases
  file/using overhead without reducing lines.
- **Merging the two pipelines behind a generic “apply patch” abstraction.** `refactor.md` explicitly says to
  keep them explicit, and the abstraction would likely add lines.
- **Replacing the hand-rolled argument parsing or logging with frameworks.** `System.CommandLine` /
  `Microsoft.Extensions.Logging` would add more code than they remove.
- **Aggressively one-lining code.** The repo already uses collection expressions, primary constructors, and
  records well; further compression hurts readability more than it helps.
