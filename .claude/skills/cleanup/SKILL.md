---
name: cleanup
description: Audit the FlickGit codebase for dead code, misplaced code, assembly-boundary violations, unsafe or human-readable Git calls, missing dependency injection, wrong or missing abstractions, compatibility shims, and over-complication — then apply the approved fixes. Use when asked to clean up, tidy, simplify, shrink, or review the structure of the project or one of its assemblies.
---

# Cleanup

Two phases, always in this order: **audit → report → (user picks) → apply → verify**.
Never start editing before the report is on screen and the user has chosen what to fix.

## Arguments

`/cleanup [scope] [--apply | --report-only]`

- `scope` — a path, a project (`Core`, `App`, `Cli`, `Shell`, `Shared`, `Tests`), or a folder inside one
  (`Diff`, `Forges`, `Views`, `Rendering`). Default: the whole `src/` tree. **Prefer a scoped run** — a
  whole-repo audit produces a report nobody acts on.
- `--report-only` — stop after the report.
- `--apply` — the user pre-approves everything in the `Safe` bucket; still ask about `Behavioral` and `Structural`.

## Ground rules

These come from `CLAUDE.md` and override any instinct to the contrary.

- **Deleting dead *code* is the point of this skill.** Hard Requirement 1 applies in full: no shims, no
  `[Obsolete]`, no deprecation path, no migration, no alias, no "legacy" branch, no setting that keeps the
  old behaviour reachable. Delete outright and change the callers. Do not flag a change as breaking.
- **Destroying the user's work is never the point.** The **Safety Rules** section of `CLAUDE.md` is absolute.
  If a cleanup would introduce or newly reach `reset --hard`, `clean -fd`, `clean -fdx`, `checkout -- .`,
  `restore .`, `branch -D`, `push --force`, `add -A`, `add .`, or a file write outside the resolved
  repository root, it is not a cleanup — it is a bug. Finding an **existing** one is a **top-severity
  finding**, reported and never "fixed" by making it tidier.
- **Simplicity beats cleverness.** Hard Requirement 2: no interface, factory, registry, event, state machine
  or config key that nothing needs today. "Might be useful later" is a rejected reason. No abstraction until
  there is a second caller.
- **The exception to "simple".** Code touching the working tree — diff reconstruction, encoding and
  line-ending preservation, `AlignedDocument`, `Hunks`/`PatchService`, `WorkingTreeWriter`, the safety
  guards — is held to *legible and verifiable*, not *short*. Do not shrink these by making them clever, and
  do not collapse a guard into a one-liner.
- **Surgical.** No reformatting, no taste renames, no touching adjacent code, no new dependencies. The
  dependency list is fixed at three (AvalonEdit, DiffPlex, H.NotifyIcon) plus
  `Microsoft.Extensions.DependencyInjection`. Every changed line traces to a specific numbered finding.
- **The long comments in the `.csproj` files, `Directory.Build.props`, `Package.wxs` and the `FlickGit.Shell`
  vtables are load-bearing documentation, not clutter.** They record *why* a switch or a slot number is what
  it is. Never delete one as part of a cleanup; if the code it describes goes, the comment goes with it and
  the report says so.
- **Never touch**: `obj/`, `bin/`, `artifacts/`, `.git/`, `*.g.cs`, `*.g.i.cs`, generated XAML partials,
  `src/FlickGit.App/Resources/*.ico`. Never renumber a `FlickGit.Shell` vtable slot as a tidy-up — a wrong
  slot takes the desktop down, and the comment naming the count that produced it stays.

## Phase 1 — Map the scope

Read before judging. For each project in scope: its `.csproj` (references, packages, and the MSBuild guard
targets), its folder layout, and `src/FlickGit.App/App.xaml.cs` — **the composition root, and the only file
allowed to mention the container**. That is where DI truth lives; read the whole `ConfigureServices` before
calling anything a missing registration.

Repo shape you are auditing against:

```
src/Shared/            IpcMessages.cs, ShellCommandIds.cs — compiled into both exes by <Compile Include>,
                       never referenced as an assembly.
src/FlickGit.Cli/      Native AOT -> flick.exe. Parses args, writes the pipe, exits. No ProjectReference,
                       no PackageReference, no Git logic, ever.
src/FlickGit.App/      WPF -> FlickGit.exe. App.xaml.cs (composition root) | CommandLine | Views |
                       ViewModels | Rendering | Resident | Trigger | Ai | Shell | Settings |
                       Localization | Languages | Tray | Infrastructure
src/FlickGit.Core/     net9.0, no UI. Every Git call, every parser, every sequence. The only tested assembly.
src/FlickGit.Shell/    Native AOT COM DLL loaded into explorer.exe. No ProjectReference, no Git logic
                       beyond GitHead.cs, no HTTP, no WPF.
src/FlickGit.Setup/    WiX. Not in FlickGit.sln. The sequence in Package.wxs *is* the package.
tests/FlickGit.Core.Tests/  The only test project. References FlickGit.Core and nothing else.
```

The safety net is **`dotnet build` + the Core test suite, and nothing more**. There is no test for
`FlickGit.App`, `FlickGit.Cli` or `FlickGit.Shell` and Hard Requirement 4 forbids adding one — say so in the
report and name what needs running by hand.

## Phase 2 — Audit passes

Run all eight. Record every finding as `file:line`, not prose.

### A. Safety and Git-call discipline — highest severity, report first

This pass outranks every other finding in the report, even when it produces no LOC delta.

1. **The forbidden commands.** Grep the whole scope for `reset --hard`, `clean -fd`, `checkout -- .`,
   `restore .` (with no pathspec), `branch -D`, `push --force`, `--force-with-lease`, `add -A`, `add .`,
   `-r` in a `git rm`. Each one must either not exist or sit behind an explicit in-the-moment confirmation.
   `branch -D` is reachable only from an answer to Git's own unmerged refusal; force-push is offered by no
   surface at all.
2. **Every read carries `--no-optional-locks`.** A read without it produces `index.lock` contention against
   the user's IDE. Check every `status`, `diff`, `log`, `blame`, `for-each-ref`, `config --list`,
   `rev-parse`, `ls-remote`.
3. **No human-readable Git output is parsed.** `git remote -v`, `git submodule status`, plain `git status`
   and `git diff --stat` are forbidden outright. Machine formats only: `--porcelain=v2 -z`, `--numstat -z`,
   `--name-status -z`, `--porcelain` for blame, `config --list -z`, an explicit `git log --format`.
4. **`ProcessStartInfo.ArgumentList` only.** No command string built by concatenation or interpolation,
   anywhere. No placeholder substituted into a joined string rather than into a list entry. No path split on
   a space.
5. **Diff reads carry `--no-color --no-ext-diff --no-textconv`**, and `-c core.quotepath=false` reaches every
   call.
6. **stdout and stderr are read concurrently.** A sequential read deadlocks on a large diff.
7. **Working-tree writes.** Every write resolves inside the repository root, refuses a symlink or junction,
   goes through the temp-file + `File.Replace` path, and preserves encoding, BOM, dominant line ending and
   trailing-newline presence. A write that reconstructs any of those from a default is a finding.
8. **Nothing in the log window or `BlameService` writes.** Both reach Git only through `ReadAsync`.
9. **Nothing blocks a window on the network.** No `fetch`, no `ls-remote`, no AI request, no forge call
   before a window paints or while Explorer builds a menu.
10. **Never logged:** API keys, credentials, diff contents, file contents, commit message bodies. Grep the
    logging calls, not just the log helper.

A finding here is reported even when the answer is "leave it alone and write a test for it".

### B. Dead code

- **C#:** types, methods and properties with no reference; a `services.AddSingleton<T>()` in `App.xaml.cs`
  for a type nothing resolves; a `PackageReference` nothing imports; a `ProjectReference` nothing uses;
  unused `using`s; commented-out blocks; models and records with no producer or no consumer.
- **Verbs with no route.** Cross-check every `Verb` / `VerbKind` value against `VerbRunner`, against the
  `flick …` list in `CLAUDE.md`, and against `Resources/Help.md`. A verb the runner cannot reach is dead;
  a verb the runner handles but `Help.md` never names is a documentation finding, not a deletion.
- **Actions with no surface.** Every `GitAction` id must be reachable from `ActionSurfaces`, from the
  registry projection in `Shell/`, or from `flick run <id>`. An action reachable from none is dead.
- **Localization.** A key in `en.lang` that no `Strings.Get` call and no XAML reference uses is dead. A
  `Strings.Get` key absent from `en.lang` is worse — it renders as a raw key name. A key present in
  `de/es/fr/it/pt.lang` but absent from `en.lang` is dead in every language.
- **Icons.** An `.ico` in `Resources/icons/` no `IconFileName` names is dead; an `IconFileName` with no file
  behind it is a broken menu entry.
- **Settings keys.** A property on the settings record nothing reads is dead — and per Hard Requirement 1 it
  is *deleted*, with `schemaVersion` bumped, never left in place for an old file's sake.
- **Tests referencing nothing.** A test whose subject you are deleting goes with it, in the same batch.

### C. Assembly boundaries and code location

This is the pass the user cares most about. Check, in order:

1. **`FlickGit.Core` references no UI assembly** and stays on `net9.0`, not `net9.0-windows`. The
   `GuardCoreHasNoUiDependencies` target enforces the references; it does *not* catch a `Windows.` P/Invoke,
   a registry read or a `Process.Start` of something that is not `git.exe`. Look for those by hand.
2. **`FlickGit.Cli` and `FlickGit.Shell` carry no reference at all** — no `ProjectReference`, no
   `PackageReference`. Anything shared arrives as a `<Compile Include>` from `src/Shared/`. A second
   hand-maintained copy of a GUID, a pipe name or a wire type is a finding: it belongs in `src/Shared/`.
3. **Nothing in `FlickGit.Shell` does work.** No Git logic beyond `GitHead.cs`'s file read, no HTTP, no WPF,
   no AI, no state check that can exceed 20 ms. `DllCanUnloadNow` returns `S_FALSE` forever.
4. **Sequences live in Core, not in a view model.** Anything whose *order* matters — stage, switch, verify,
   commit; push, then create the pull request; deinit then `git rm`; bin then restore — belongs in
   `FlickGit.Core` where it can be tested. Order logic found in a `ViewModels/` or `Views/` file is a
   Structural finding, and name the Core type it belongs in.
5. **View models own presentation only.** No Git process call, no `File` I/O, no argument list in
   `ViewModels/`. Business logic in XAML code-behind is a violation, full stop.
6. **Layer placement inside App.** `Views/` is windows and controls; `ViewModels/` is presentation state;
   `Rendering/` is renderers, gutters and `AlignedDocument` — the only thing converting between the padded
   editor document and the file on disk; `Resident/` is the pipe, tray, notifier and window hosts;
   `CommandLine/` routes verbs and nothing more. `AiTextService` sits in `App/Ai` on purpose because it
   reads settings and the credential store — do not "fix" that by moving it into Core.
7. **Core folder ownership.** A file that parses `--porcelain=v2` belongs in `Status/`, a patch generator in
   `Diff/`, a forge client in `Forges/`. Flag a type sitting in a folder that does not own its subject, and
   name the destination file.
8. **Duplication.** The same parse, the same argument list or the same guard written twice inside Core →
   extract locally. Written once in Core and once in App → the App copy goes and the call goes to Core. Two
   similar-looking things answering to different owners — a read of `.git/HEAD` in `FlickGit.Shell` and
   `BranchService` in Core — are **not** duplication; that one is deliberate and stays.

For each violation, name the correct destination file, not just "it's in the wrong place".

### D. Dependency injection

Hard Requirement 3. Every collaborator arrives as a **typed constructor parameter**, registered in
`App.xaml.cs`.

- Flag: `new SomeService(...)` inside another service or a view model, `new HttpClient()` anywhere but the
  warm `SocketsHttpHandler` setup, `GetRequiredService` outside `App.xaml.cs`, and static mutable state.
- **Never `new` a collaborator that does I/O** — a process, a file, the registry, the network, the clock.
  Value objects, records, view models and windows are *not* collaborators; `new` those freely and do not
  report them.
- **`IServiceProvider` is never injected.** A constructor taking eight services is a class doing two jobs —
  say which two, and where the split falls.
- **Per-invocation state is a parameter, not a field.** A field holding one call's repository path, token or
  cancellation token is what stops a service being a singleton — flag it and name the method signature it
  should move to.
- **Statics that are right, and stay.** One that merely *names* a location (a settings path, a pipe name), a
  pure function of its arguments (a parser, a matcher, a validator, `CommitRange.Resolve`,
  `PullRequestPrompt.Split`), and the thinnest wrapper over a process-global OS facility with one
  implementation forever (the console, the clipboard). Do not inject these to satisfy a rule. Say so if
  tempted.
- **Logging goes through `ILog`**; no `Console.WriteLine` outside the CLI's own console path.
- Configuration arrives as the settings record, not as scattered file reads.

### E. Abstractions, shims and speculative code

Add an interface **only** when one of these is true today:

- there is a second real implementation (`IAiGenerator`: five of them; `IGitProcessRunner`: the real one and
  the test fake), or
- it is the seam a test needs in order to assert arguments instead of starting `git.exe`.

That is the whole list. Flag an interface with a single implementation, one caller and no test seam — that is
a **surplus** abstraction, inline it. Flag a factory, a registry, a strategy hierarchy or an event where a
method call, a field or a `switch` would do.

Also flag, and delete outright:

- `[Obsolete]`, "legacy" paths, a second code branch for the way it used to work, commented-out previous
  versions.
- Migration or conversion code for an older `settings.json` / `actions.json` shape, and any default that
  quietly stands in for a removed key. An unknown `schemaVersion` is **refused**, never migrated.
- An alias or deprecated spelling for a verb, an action id, an IPC message, a registry value or a pipe name.
- A setting nobody asked for. A named constant with a comment beats a config key, a settings row and a
  persisted field.
- A parameter, overload or option with one caller passing one value.

### F. Simplification

Target: fewer lines doing the same thing. Report a concrete **LOC delta** per finding — that number is the
point of the pass.

- Hand-rolled work the framework or a dependency already does. DiffPlex computes the diff; do not find a
  Myers implementation and keep it.
- Deep nesting → early return. Long methods → named smaller ones. But **do not split a class that is easier
  to read whole**.
- Error handling for scenarios that cannot occur; `try`/`catch` that only rethrows; a catch that swallows a
  Git error is not a simplification finding, it is a pass-H **Behavioral** one.
- Collection and LINQ work that allocates or enumerates the same sequence twice — and in a parser or a
  renderer, say whether the path is hot.
- Async discipline: `CancellationToken` threaded end to end, no `.Result`, no `.Wait()`, no `async void`
  outside an event handler.

**Performance-shaped simplifications, weighed against the targets table:**

- Work on a paint path that could be deferred, and work deferred that the first frame actually needs.
- A `git` call whose answer is already in hand — the resolved root, the cached overview, the numstat counts
  the AI payload is about to recompute with `diff --stat`.
- A per-keystroke enumeration of refs, or a scan that is not parallel.
- A `FileSystemWatcher` on a working tree (forbidden), a `SetProcessWorkingSetSize` call (forbidden), a
  `GetForegroundWindow` call inside an input hook proc (forbidden).
- A cache added with no measured hot path and no stated invalidation rule.
- **Pre-warm re-initialisability:** every mutable field of a pre-warmed view model must be assigned in its
  `Reset`. A field that is not is a state leak between two uses of the window — a **Behavioral** finding, and
  the highest-value one this pass produces.

### G. Test scope

Hard Requirement 4 cuts both ways, and both directions are findings.

- **Out of scope, so delete:** any test that constructs a `Window` or touches `FlickGit.App`; a test for IPC
  framing, the tray, the registry writer, logging, notifications, autostart or the installer; a test for
  clone, fuzzy matching, a diff renderer or repository detection; a test that starts a real `git.exe`; a
  second test for a rule that already has one.
- **In scope and missing, so add:** a parser without a spaces-and-non-ASCII case; a sequence
  (`CommitFlow`, `PullRequestFlow`) whose order nothing asserts; a safety rule with no test —
  a blocked switch changing nothing, a stash restoring only its own, a diverged push refused, `add -A`
  absent from every argument list, `branch -D` absent unless forced, untracked and secret-matching files
  unstaged by default, `--no-optional-locks` on every read; an encoding, BOM or line-ending round trip; the
  AI payload builder; a provider stream read a few bytes at a time; a command-line grammar case.
- A new test needs one sentence naming the in-scope bullet it belongs to. Put that sentence in the report
  row. If there is no such sentence, do not propose the test.

### H. Consistency and error handling

Match what is already there — records for immutable results, `Strings.Get` for every user-facing string,
`ILog` for every log line, async all the way with `CancellationToken` threaded.

- **No generic error message.** Every failure path names the operation, the Git error, the repository path
  and a next action. A "Something went wrong", a bare exception message, or a `catch` that discards
  `GitResult.StdErr` is a finding here and it is **Behavioral**.
- **One word per meaning:** `common.close` for a button that only dismisses, `common.cancel` only for one
  that stops something. A hard-coded English string in XAML or C# is a finding.
- **Exit codes** are the documented five; a verb inventing a sixth is a finding.
- Consistency findings are the lowest priority. Never let them crowd out A–E.

## Phase 3 — Report

Output one markdown table, findings numbered, grouped into three buckets:

| Bucket | Meaning |
|---|---|
| **Safe** | Pure deletion or mechanical move. The build and the Core suite prove it. No behavior change. |
| **Structural** | Moves code across files or projects, adds or removes an abstraction. Behavior preserved by intent. |
| **Behavioral** | Could change what the running tool does. Needs manual testing — name the test. |

Each row: `#`, bucket, `file:line`, what is wrong, the fix, LOC delta, and the risk if any. Sort by value,
not by file order, with every pass-A finding above everything else regardless of its LOC delta. Then state
the totals (findings, estimated LOC removed) and ask which numbers to apply.

If a finding is uncertain — you could not prove a symbol is unreferenced because it is reached from XAML, a
`.lang` key, a registry value, an `actions.json` id, reflection, or a `<Compile Include>` in another
project — say so in the row and default to **not** deleting it. XAML and `.lang` are the two that catch
people out here: grep the `.xaml` files and every `.lang` file before calling a C# symbol or a resource key
dead.

## Phase 4 — Apply

Only the approved numbers. Batch by concern (all dead code, then all moves, then the rest), not by file.
Verify after each batch, not at the end. If a batch fails to build, fix it or revert that batch — never leave
the tree broken while starting the next one.

Deleting a settings key, an action id, an IPC message or a verb means **bumping `schemaVersion` and writing
no migration**. That is the correct outcome, not an oversight.

## Phase 5 — Verify

```powershell
dotnet build FlickGit.sln -c Debug
dotnet test tests/FlickGit.Core.Tests/FlickGit.Core.Tests.csproj -c Debug
```

`dotnet build` does **not** exercise Native AOT — it only applies on publish. If the batch touched
`FlickGit.Cli`, `FlickGit.Shell` or `src/Shared/`, the trim and AOT warnings only appear here, and this needs
the MSVC linker:

```powershell
dotnet publish src/FlickGit.Cli/FlickGit.Cli.csproj -c Release -r win-x64
dotnet publish src/FlickGit.Shell/FlickGit.Shell.csproj -c Release -r win-x64
```

Then report honestly: what was applied, the actual LOC delta, what was skipped and why, and the exact manual
checks the `Behavioral` changes now require. Draw those from **Definition of Done** — it works from Explorer,
it works from the CLI, it works with the resident service stopped, paths with spaces and Unicode work,
cancellation is safe, no encoding or line ending is rewritten — plus `flick diag timings` and
`flick diag doctor` for anything that touched a measured path.

If a build, a publish or a test run fails, show the output — never claim a clean run you did not see.
