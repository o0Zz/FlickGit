# CLAUDE.md — FlickGit

## What we are building

A native Windows Git productivity tool in C#, integrated into Windows Explorer. Fast Git workflows
without TortoiseGit, Fork or GitKraken.

The target user works across 5–10 repositories a day and switches constantly. The bottleneck being
solved is **not** the commit dialog — it is the cost of getting to the right repository and back out
again. Every design decision follows from that.

- Git actions from the Explorer context menu
- A global hotkey that opens the commit window on the folder Explorer is showing
- A commit window with a TortoiseGit-style file list (status, lines added, lines removed)
- A side-by-side diff viewer with a **live-editable** right pane, and line/hunk staging
- AI-generated commit messages, pull-request descriptions and changelogs
- Branches, tags, submodules, clone, pull --rebase, push, pull requests
- History: a log with a combined diff over a selection, and blame with a walk back through `previous`
- Clear, non-destructive error handling

Optimise for **speed, clarity, safety, minimal UI** — in that order, and never at the expense of
safety.

This is not a complete Git client.

> Make the most common safe Git operations available in zero or one clicks, from wherever the user
> already is.

Whenever a workflow becomes complicated, expose the repository state clearly instead of being clever
or silently modifying it.

---

# Hard Requirements

Four rules that override convenience, habit, and anything elsewhere in this document that reads like
a suggestion.

## 1. Break things freely

**There is no backwards compatibility in this project.** Not with a previous version, not with a
config file an earlier build wrote, not with anything FlickGit has ever shipped. This is not a
tolerance for breaking changes — it is a requirement to make them whenever the new design is better.
Do not ask whether a change is breaking. Ask only whether it is right.

- **Settings, `actions.json`, cached state** — change the shape, drop keys, rename them. Bump
  `schemaVersion` and let an old file be refused. **Never write a migration, a converter, an upgrade
  path or a default that quietly stands in for a removed key.**
- **Registry layout, CLI verbs, exit codes, action ids, IPC messages, pipe names, file layout** —
  rename or remove them outright. No aliases, no shims, no fallbacks, no deprecated spellings.
- **Types in `FlickGit.Core`** — there is no external consumer. Change the signature rather than
  adding an overload beside it; rename the type rather than keeping the old name pointing at it.
- **Delete rather than deprecate.** No `[Obsolete]`, no "legacy" path, no second code branch for the
  way it used to work, no commented-out previous version.
- **An old install is not a constraint.** Reinstalling, re-registering the menu, re-entering an API
  key or deleting `%LOCALAPPDATA%\FlickGit` are acceptable upgrade costs.

Do not propose compatibility as a courtesy, do not flag a change as breaking, do not leave the
previous behaviour reachable behind a setting.

**What this does not license.** Breaking changes are about *our* formats and *our* interfaces — never
the user's data. Everything under **Safety Rules** holds unconditionally.

## 2. Build only what is asked for

Solve the problem in front of you, at the size it actually is.

- **No abstraction until there is a second caller.** One implementation needs no interface, no
  factory and no registry.
- **No setting nobody asked for.** A named constant with a comment beats a config key, a settings row
  and a persisted field.
- **No generality for its own sake.** Two similar cases stay two cases until a third makes the
  pattern real.
- **Prefer the boring mechanism.** A method call over an event, a field over a state machine, a
  `switch` over a strategy hierarchy.
- **Do not split a class that is easier to read whole.**

The exception is code touching the user's working tree — diff reconstruction, encoding and
line-ending preservation, staging, and the safety guards. There, "simple" means *legible and
verifiable*, not *short*.

## 3. Everything by constructor injection

A class receives what it needs as typed constructor parameters. Not a container, not a static, not
something it fetches for itself.

- **Never inject `IServiceProvider`.** A constructor needing eight services is a class doing two jobs
  — split it.
- **Never `new` a collaborator that does I/O** — processes, files, the registry, the network, the
  clock. Value objects, view models and windows are not collaborators; `new` those freely.
- **Statics that *do* something become services.** Three kinds of static stay: one that merely *names*
  a location (a settings path, a pipe name), a pure function of its arguments (a parser, a matcher, a
  validator), and the thinnest possible wrapper over a process-global OS facility with exactly one
  implementation forever (the console, the clipboard).
- **Per-invocation state is a parameter, not a field.** That is what lets the services using it be
  singletons with nothing to reset between uses.
- **Register in one place.** `App.xaml.cs` is the only file that mentions the container.

## 4. Test the core, not everything

`tests/FlickGit.Core.Tests` is the only test project and there will not be a second. It targets
`net9.0`, references `FlickGit.Core` and nothing else, and **fakes `IGitProcessRunner` rather than
starting `git.exe`** — so the *arguments* are assertable, which is the half a temporary repository
would hide.

**In scope, and only this:**

- **Parsers and the pure functions beside them** — `--porcelain=v2 -z`, `--numstat -z`,
  `--name-status -z`, the `git log` format, `blame --porcelain`, `config --list -z`, `ForgeUrl`,
  `CommitRange.Resolve`. Include paths with spaces and non-ASCII bytes.
- **The sequences** — `CommitFlow` (stage, switch, verify, commit, push) and `PullRequestFlow` (push,
  then create).
- **The safety rules.** A blocked switch changes nothing; a stash restores only the one it created; a
  diverged push is refused; `add -A` never appears in an argument list; `branch -D` never appears
  unless force was asked for; untracked and secret-matching files are not staged by default; every
  read carries `--no-optional-locks`.
- **The working tree.** Encoding, BOM and line-ending round trips, line reverting, and the one value
  that may ever be written to a file.
- **What may leave the machine** — the AI payload builder, and the provider streams read a few bytes
  at a time so a reader that only works on a whole response does not pass.
- **The command-line grammar**, because every surface goes through it.

**Out of scope. Do not add tests for these:**

- **Anything in `FlickGit.App`.** No view models, no windows, no XAML, no WPF test project. A test
  that has to construct a `Window` is testing WPF.
- **Plumbing** — IPC framing, the tray icon, the registry writer, logging, notifications, autostart,
  the installer.
- **Secondary features** — clone, fuzzy matching, the diff renderers, repository detection.
- **A real `git.exe`.**
- **A second test per rule.** One test per behaviour.

Everything out of scope is verified by **running it** — start the service, run the verb, read
`flick diag timings`, confirm the numbers against **Performance Targets**.

A new test needs a sentence saying which in-scope bullet it belongs to. If there is no such sentence,
do not write it.

---

# Technical Direction

- C#, .NET 9 or newer, WPF for UI, Native AOT for the CLI stub and the shell DLL
- **Git CLI as the source of truth** — call the installed `git.exe`, never reimplement Git, and never
  add a libgit2 binding beside it
- Async process execution throughout; native Windows shell integration
- **Minimum Git 2.23** — `switch` and `restore` are used unconditionally, with no fallback to
  `checkout` or `reset`

FlickGit itself is **Apache 2.0** — `LICENSE` at the root and `src/FlickGit.Setup/License.rtf`, which
is the MSI's licence page, carry the same text and must not drift apart.

Third-party dependencies must be permissively licensed, and the list is fixed at three:
**AvalonEdit** (MIT, editor control), **DiffPlex** (Apache 2.0, line/word diff) and
**H.NotifyIcon** (MIT, tray icon, avoids WinForms). No Electron-style or web UI layer. A minimal
`Microsoft.Extensions.DependencyInjection` container is fine; nothing heavier.

---

# Architecture

Business logic never runs inside `explorer.exe`. Every shell surface is a thin trigger that launches
the CLI stub, which forwards to the resident service.

```text
FlickGit.sln

src/
├── Shared/                  Compiled into *both* executables by <Compile Include>.
│   └── IpcMessages.cs       The pipe's wire format. Shared as source, not as a third
│                            assembly, because the AOT stub must not carry a reference.
│
├── FlickGit.Cli/            Native AOT -> flick.exe. No WPF, no WinForms.
│   └── Parses args, connects to the named pipe, exits. Falls back to launching
│       FlickGit.App directly.
│
├── FlickGit.App/            WPF -> FlickGit.exe. Resident, single instance, tray icon.
│   ├── App.xaml.cs          Composition root and process lifecycle. Nothing else.
│   ├── CommandLine/         RepositoryVerbs answer in text about a repository,
│   │                        EnvironmentVerbs about the installation, WindowVerbs open
│   │                        something and stay. VerbRunner routes; VerbOutput decides
│   │                        between console and window and is passed per call.
│   │                        ActionRunner executes a catalog action.
│   ├── Views/               Windows, the diff pane, PopupPlacement.
│   ├── ViewModels/          Presentation state. No Git logic.
│   ├── Rendering/           Diff renderers, gutters, DiffBrushes, and AlignedDocument --
│   │                        the only thing converting between the padded editor document
│   │                        and the file on disk.
│   ├── Resident/            Pipe server, tray, notifier, window hosts. AppWindow is the
│   │                        pre-warm and the show sequence.
│   ├── Trigger/             Global hotkey and Explorer folder resolution.
│   ├── Ai/                  AiTextService: failure counter and streaming state machine.
│   │                        Here rather than Core because it reads settings and the
│   │                        credential store.
│   ├── Shell/               Registry projection of the Action Catalog, and
│   │                        OverlayIntegration -- the only HKLM write in the product.
│   └── Settings/ Tray/ Localization/ Infrastructure/ Languages/
│
├── FlickGit.Core/           net9.0, no UI dependencies. The only tested assembly.
│   ├── Cli/                 Verb, VerbKind, ExitCodes -- the command-line grammar.
│   ├── Git/                 GitProcessRunner, GitExecutable, errors
│   ├── Repositories/        RepositoryService
│   ├── Status/              porcelain v2, numstat, name-status parsing, StatusService
│   ├── Diff/                DiffService, FileTextLoader, WorkingTreeWriter, DiffDocument,
│   │                        Hunks + PatchService (patch generator, `git apply --cached`)
│   ├── Files/               TrackingService -- `git add`/`git rm` on one path, never forced
│   │                        and never a pathspec that can glob; FolderRemovalFlow, whose
│   │                        order is the safety rule (gate, ask, bin, record)
│   ├── Commits/             CommitService, CommitFlow
│   ├── Blame/               BlameService, BlamePorcelainParser
│   ├── History/             HistoryService, CommitLogParser, CommitRange
│   ├── Actions/             ActionCatalog, GitAction, ActionRun, ActionSafety,
│   │                        ActionPlaceholders, actions.json
│   ├── Palette/             RepositoryScanner and the cached overview
│   ├── Ai/                  DiffPayload and AiContextBuilder (what may leave the machine),
│   │                        four providers over AiEndpoint, IAiGenerator taking a prompt,
│   │                        PromptStore + the three built-in prompts
│   ├── Forges/              ForgeUrl, PullRequestService, PullRequestFlow, three clients
│   │                        over one ForgeApi, GitCredentialFill
│   ├── Branches/            BranchService, SwitchService
│   ├── Stashes/             StashService + GitStash. The list is positional, so every pop
│   │                        and drop re-reads it and checks the sha first
│   ├── Config/              RepositoryConfigService, GitConfigList
│   ├── Remotes/             PushService, RemoteService
│   └── Pulls/ Clone/ Secrets/ Matching/ Logging/ Diagnostics/ Models/
│
└── FlickGit.Shell/          Native AOT COM DLL, loaded into explorer.exe. Draws the whole
    │                        FlickGit block and the repository badge. Hand-rolled
    │                        vtables, no [GeneratedComInterface]. No ProjectReference.
    ├── Exports.cs              DllGetClassObject, DllCanUnloadNow, IClassFactory
    ├── ContextMenuHandler.cs   IContextMenu + IShellExtInit
    ├── OverlayHandler.cs       IShellIconOverlayIdentifier. A second CLSID, no state
    ├── Selection.cs            The clicked folder or file, from a PIDL or CF_HDROP
    ├── MenuRegistry.cs         The menu, as the App wrote it into the CLSID key
    ├── MenuIcons.cs            An .ico as a 32bpp menu bitmap
    ├── GitHead.cs              The branch, from .git/HEAD, and HasGitEntry -- the one
    │                           syscall the overlay is allowed. No git.exe, no pipe.
    └── RepositoryLookup.cs     One answer per right-click instead of four

src/FlickGit.Setup/          WiX -> FlickGit-<version>-x64.msi. Per-user, no elevation. Not
                             in FlickGit.sln: it packages publish output rather than
                             compiling sources.

tests/FlickGit.Core.Tests/   The only test project. See Hard Requirement 4.
```

**Sequences belong in Core, not in a view model.** Anything with an order that matters — stage,
switch, verify, commit; plan, consent, push — goes in `FlickGit.Core` and gets tests. A view model can
only be exercised by clicking, and "the steps happened in the wrong order" is exactly the bug clicking
does not reveal. View models own presentation only.

Assembly names differ from project names on purpose:

| Project         | Output              | Why |
|-----------------|---------------------|-----|
| `FlickGit.Cli`  | `flick.exe`         | The command the user types, and the one written into the registry. |
| `FlickGit.App`  | `FlickGit.exe`      | The resident process. Its first icon is the context menu's root icon. |
| `FlickGit.Core` | `FlickGit.Core.dll` | `net9.0`, not `net9.0-windows`: the no-UI rule is structural, not a review convention. |

**Both executables and `FlickGit.Shell.dll` must sit in the same directory.** `flick.exe` resolves
`FlickGit.exe` beside itself, and the registry names `flick.exe` and `icons\*.ico` by path.

**Process split.** `flick.exe` **must** remain Native AOT — a framework-dependent stub costs 50–100 ms
of CLR startup on its own, which defeats the point. `FlickGit.exe` pays WPF startup once, at login,
and keeps pre-warmed windows alive.

## Resident service

The single biggest lever on perceived speed. A cold WPF start costs 400–800 ms; pay it once at login.

- **Warm-up**: construct the commit window and the palette without calling `Show()`, force a
  measure/arrange pass so WPF resolves themes and JITs the layout path, and keep the instances alive —
  on request, reset the `DataContext` and `Show()`. Also warm AvalonEdit, the AI provider connection
  and the repository-root cache.
- **IPC**: named pipe `\\.\pipe\flickgit.{userSid}.{sessionId}`, length-prefixed UTF-8 JSON, one
  request one response, client timeout 250 ms. `PipeSecurity` grants the **current user SID only** —
  this pipe can trigger process execution through user-defined actions.
- **On timeout or a missing pipe, the CLI launches `FlickGit.exe` directly with the same arguments.**
  The resident service is an optimisation, never a dependency. Every feature must work without it.
- **Foreground activation**: a background process cannot steal focus, so the CLI — which holds
  foreground rights — calls `AllowSetForegroundWindow(residentPid)` **before** sending the request and
  exits only after the response. `WM_HOTKEY` grants the rights directly; a low-level hook grants
  nothing, so anything opened from one must *check* `GetForegroundWindow` after activating.
- Single instance via named mutex. Autostart is a Scheduled Task at logon with a 30–60 s delay. Idle
  working set target **80 MB**; never fake it with `SetProcessWorkingSetSize`. **No
  `FileSystemWatcher` on working trees** — status is computed on demand and cached briefly.

## Shell integration

Every shell surface is a thin trigger; none contains logic; all launch `flick.exe`.

The menu is **one `IContextMenu` handler** (`FlickGit.Shell.dll`), registered under
`Directory\shellex\ContextMenuHandlers\FlickGit` and the same for `Directory\Background`, `Drive` and
`*`. Static registry verbs cannot reach the block Explorer reserves for shell extensions, which is
where every Git client sits — a verb only reaches `Top`, the default, or `Bottom`.

Rules for anything loaded into `explorer.exe`:

- **No Git logic, no AI SDK, no WPF, no HTTP client.** Any state check answers from a cache within
  **20 ms**, hard timeout **50 ms**, falling back to "show" rather than blocking.
- **Native AOT, not `comhost`** — the alternative loads the CLR into `explorer.exe`.
- **`DllCanUnloadNow` returns `S_FALSE` forever.** The .NET runtime cannot be reinitialised in a live
  process. The cost is that the DLL is locked while Explorer lives, which is why there is an installer.
- **Vtable slot numbers are the risk.** Comment every slot with the count that produced it, and test
  changes **out of process first** through a throwaway CLSID — the same bug found by registering the
  handler takes the desktop down.
- **Every registry key the tool creates is named `FlickGit.*`**, so an uninstall finds them by
  enumerating one prefix. Never enumerate or modify keys the tool did not create.
- `install-shell` refuses when the DLL is not beside `flick.exe`. Windows 11 accepts only **one level**
  of submenu, and all of this appears only under "Show more options" — the primary menu needs a sparse
  MSIX package with package identity and a code-signing certificate, which is the one part still open.

**Not available — do not attempt.** Explorer toolbar or ribbon buttons; deskbands; a branch column in
Details view; and **status** overlay icons — per-file, or clean-versus-modified, which is the version
that needs a status per drawn item and is what makes TortoiseGit's overlays slow.

## The repository overlay

One badge, on repository roots, saying **this folder is a Git repository** and nothing else. Not clean,
not modified, not ahead of the remote, and not on the folders inside it. Every rule below follows from
that: with no status to compute there is no `git.exe`, no pipe, no cache and nothing to invalidate.

`OverlayHandler` is a **second CLSID in the same DLL**, implementing `IShellIconOverlayIdentifier` on a
six-slot vtable and holding no per-instance state — Explorer creates one at startup and keeps it for
the session, so anything remembered between calls would outlive its meaning.

**`IsMemberOf` is the hottest callback in the product**: synchronous, on the thread painting the view,
once per drawn item, forever. Its tests are in cost order and every one is an early exit — the
directory bit in the attributes it was handed (so every *file* on the machine costs one bit test), then
cloud-placeholder attributes (probing one hydrates it), then a `\\` prefix (the same refusal
`RepositoryLookup` makes, for the same redirector timeout), and only then one `GetFileAttributesW` via
`GitHead.HasGitEntry`. **No cache**: every call is a different path, so one would never hit.
`GetPriority` answers **50, not 0** — an item gets one overlay, and a git badge must not displace a sync
engine's "not uploaded yet".

**Registration is two halves, and only one needs elevation.** `HKCU\Software\Classes\CLSID\{overlay}`
carries the server and the icon path; `HKLM\...\ShellIconOverlayIdentifiers\ FlickGit` is **one string
value** and the only thing FlickGit writes outside `HKCU`. `CoCreateInstance` reads the user hive
first, which is why the elevated half knows nothing but a GUID.

- **Opt-in, never on install.** `flick install-overlay` and a settings checkbox, self-elevating by
  starting `FlickGit.exe` (not `flick.exe`, which would forward to an unelevated resident service)
  with `runas`. The MSI stays per-user and no-elevation and never registers it.
- **The leading space in the key name is load-bearing.** Windows loads the first **15** handlers sorted
  by name; a space sorts ahead of letters. `flick diag doctor` reports the position, because a
  registration past the fifteenth is invisible in every other way.
- **It does not take effect until Explorer restarts** — handlers are enumerated once at its startup and
  `SHChangeNotify` does not reload them. Every message says so.
- **The MSI uninstall cannot remove the `HKLM` key**, being unelevated. The orphan is harmless — the
  CLSID no longer resolves, so Explorer skips it — but it holds a slot, so `diag doctor` names it and
  `flick uninstall-overlay` is the way out.

---

# Command Line Interface

Every action is reachable from Explorer, a terminal, scripts and keyboard launchers, and they all route
through `Verb` and `VerbRunner` — one route to Git, so the fast surfaces cannot be shortcuts around the
safety rules.

```text
flick clone <path> [url]             clone into a subdirectory of <path>
flick commit <path>                  commit window (branch ComboBox included)
flick pull-rebase <path>             --autostash, + submodule update when applicable
flick push <path>
flick pr <path>                      open a pull request for this branch
flick switch <path> [branch]         branch picker when omitted
flick tag <path> [name]              tag window when omitted; creates and pushes when named
flick stash <path> [message]         stash window when omitted; stashes the working tree when named
flick submodule <path>               submodules: add, remove, initialise
flick status <path>
flick log <path>                     commit history; multi-select for a combined diff
flick blame <file>                   who last touched each line, and what came before
flick add <path>                     stage one file or folder, tracking what is new
flick rm <path>                      delete one file or folder and stage the deletion; asks first
flick repo <path>                    identity, remotes and this repository's defaults
flick run <id> [path]                run a catalog action by id
flick palette                        global repository palette
flick settings
flick install-shell                  register context menu entries
flick uninstall-shell
flick install-overlay [system]        badge repository folders; asks for admin once
flick uninstall-overlay [system]      `system` is the elevated half, for scripted installs
flick autostart [on|off]             logon task for the resident service
flick ai                             what the AI is configured to do
flick ai key [set|clear]             store or remove the API key
flick language [code|auto]           interface language; lists them when omitted
flick diag timings                   recent latency measurements
flick diag doctor                    environment and integration health check
```

`<path>` defaults to the current working directory when omitted. Explorer's quoted `%V` for a drive
root is unquoted.

Exit codes: `0` success, `1` Git error, `2` not a repository, `3` user cancelled, `4` configuration
error, `5` operation refused for safety.

---

# Git Command Execution

A single reusable async process runner. Every Git call in the product goes through it.

```csharp
Task<GitResult> RunAsync(
    string repositoryPath,
    IReadOnlyList<string> args,
    CancellationToken cancellationToken);

public sealed record GitResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration);
```

- Always `git -C "<repository>" ...`
- **Always `--no-optional-locks` for read operations.** Without it `git status` writes the index, which
  produces `index.lock` contention against the user's IDE.
- `ProcessStartInfo.ArgumentList` only. **Never build a command string.** No shell interpolation
  anywhere in the codebase.
- No visible console window; UTF-8 stdout/stderr; `-c core.quotepath=false` so non-ASCII paths come
  back unescaped.
- Read stdout and stderr **concurrently** — sequentially, large diffs deadlock on a full pipe.
- Full cancellation, including killing the process tree.
- `--no-color --no-ext-diff --no-textconv` on every diff read, against the user's own gitconfig.
- **Never parse human-readable Git output.** Machine formats only: `--porcelain=v2 -z`, `--numstat -z`,
  `--name-status -z`, `--porcelain` (blame), `config --list -z`, and an explicit `git log --format`.
  `git remote -v`, `git submodule status` and plain `git status` are forbidden — none has a machine
  form, so what they would have given comes from cheaper, exact sources instead.
- Paths may contain any byte except NUL. Never split on spaces.

Locate `git.exe` in this order: user setting, `PATH`, standard install locations. Cache the result, and
surface a clear error if not found rather than failing per-command.

**Repository detection** is `git -C "<path>" rev-parse --show-toplevel`. Must support the repository
root, a subdirectory inside it, and the Explorer background; normalise everything to the root. Not a
repository → a one-line message, never a full window. Cache resolved roots in the resident service,
30 s TTL, keyed by directory, invalidated on any write the tool performs.

**Submodules are detected by `File.Exists(.gitmodules)`**, cached with the root. Never run
`git submodule status` to find out whether submodules exist.

---

# Surfaces

## Commit window — the only commit surface

Reached from the context menu, the global hotkey and the CLI.

```text
┌────────────────────────────────────────────────────────────────────────────┐
│ d360-portal                              feature/storage-gw    ↑2 ↓0       │
├──────────────────────────────────┬─────────────────────────────────────────┤
│ Changed files                    │ Working tree ↔ HEAD                     │
│ ☑ M src/GatewayClient.cs +42 -17 │  43- var pool = new Pool();  │ 43+ var… │
│ ☑ A src/PgBouncerPool.cs +156 -0 │  44  return pool;            │ 44  ...  │
│ ☐ ? scratch/dump.json    +12  -0 │  ← read-only        editable →          │
├──────────────────────────────────┴─────────────────────────────────────────┤
│ Commit message                                                             │
│ feat: add PgBouncer connection pooling                [ Generate with AI ] │
├────────────────────────────────────────────────────────────────────────────┤
│ [ Commit & Push ]   [ Commit ]                              [ Close ]      │
└────────────────────────────────────────────────────────────────────────────┘
```

The instance is pre-warmed and reused, so it must be fully re-initialisable — **every mutable field is
assigned in `CommitViewModel.Reset`**. No state may leak between two uses.

```text
⏎ commit & push       ⇧⏎ newline        Ctrl+⏎ commit & push from anywhere
Ctrl+S save the edit  Ctrl+Z undo it    Ctrl+F find    F3/⇧F3 next/previous
F5 re-read status     esc close the search bar if open, otherwise the window
```

- The caret is in the message box from the moment the window is populated. Enter commits rather than
  inserting a newline, and the footer says so whenever there is no outcome to report in its place.
- **Enter is suspended while the diff pane has keyboard focus** — that pane is an editor over the
  user's working tree, where Enter is a newline in their file.
- **Esc closes, always**, cancelling a running generation on the way out. The single exception is a
  commit *already executing*, where the window must stay to report the outcome.
- **Queued Enter:** Enter pressed before the AI message arrives queues the commit, and it fires the
  instant the message lands. If generation *fails* while queued, cancel the queue and focus the
  message box. **Never commit an empty or placeholder message.**

**File list** — three commands in parallel, merged on path, warm budget **60 ms**:

```bash
git -C <repo> --no-optional-locks status --porcelain=v2 -z
git -C <repo> --no-optional-locks diff --numstat -z            # worktree vs index
git -C <repo> --no-optional-locks diff --cached --numstat -z   # index vs HEAD
```

`GitFileChange` carries the path, the old path, the index and worktree status, added/removed line
counts (`null` for binary), and the untracked, staged and selected flags. Supports modified, added,
deleted, renamed, copied, untracked, conflicted; sorted conflicted, modified, added, deleted, renamed,
untracked last. `-z` numstat emits a rename as two separate fields rather than `old => new`; binary
files report `-` rather than `0` and display `bin`; untracked files are absent from numstat and are
counted from disk with a 1 MB guard and a binary sniff.

## Staging defaults

The user may commit without reviewing the list, so the defaults must be safe without being annoying.

- **Tracked modified and deleted files: checked.**
- **Untracked files: unchecked**, count shown. This prevents `.env`, `appsettings.Development.json`,
  `bin/`, `obj/` and stray dumps being pushed in a hurry. It is the single most valuable safety default
  in the product.
- Files matching secret-detection patterns are unchecked and flagged red, even if tracked.
- Empty resulting set: say so and disable Commit.

Stage with `git add -- "<file>"`, unstage with `git restore --staged -- "<file>"`. **Never `git add -A`
or `git add .`** anywhere in the product — stage the explicit resolved path list.

**Three staging states mean "leave the index alone":** a whole ticked file is an ordinary `git add`;
`HasChosenHunks` means the index already holds exactly what was picked; and `IsDeletionStaged`
(porcelain `1 D.`, a `git rm`-ed path) must be kept out of `git add`, where the pathspec matches
nothing and aborts the whole commit. Porcelain `1 .D` — gone from the worktree, index entry still
there — *is* passed to `git add`.

## Delete and revert, from the file list

Both take a multi-selection, both **send the copy on disk to the Recycle Bin first**, and both loop one
path per call so the first failure stops with a count of what went before.

**Delete file…** runs no Git command at all. An untracked file is uncommitted work Git has never seen,
so `git restore` cannot bring it back — the Recycle Bin is what makes the operation recoverable, and
what earns it one question. Rows whose file is already gone are filtered out of the selection. Two
refusals: nothing outside the resolved root (`WorkingTreeWriter.ResolveInsideRepository`), and no
symlinks or junctions.

**Revert file…** is `git restore --source=HEAD --staged --worktree -- "<file>"`. `--source=HEAD`
because the default restores from the *index* and would leave a staged change standing. **This is the
only place in the product that asks Git to discard uncommitted work**, which is why the bin comes first
— a locked file fails the bin, and failing there means nothing has happened yet. Rows HEAD does not
have are **skipped, not refused**: untracked (Delete is the item for those), added (the command would
delete the file with exit 0 and no message), renamed and copied (HEAD has the old path), and
conflicted (taking HEAD's side is a merge decision). `RestoreService` owns that predicate.

## Diff viewer

**The right pane is editable, so `git diff` output cannot be the rendering source** — the moment the
user types a character any hunk list from Git is stale. The viewer diffs two in-memory buffers with
DiffPlex and recomputes on edit, debounced 200 ms, off the UI thread.

The left-hand base is **always HEAD** (`git show HEAD:<path>`), empty for an untracked file. In the log
window it is the commit range instead, and `SideBySideDiff.Range` carries it — non-null means read-only
and supplies the header text, so a historical diff cannot be rendered under a "Working tree ↔ HEAD"
label.

- **Do not use DiffPlex's side-by-side alignment.** It pairs a block's deletions with its insertions
  positionally, so a plain replacement lands red and green on different rows when the counts differ.
  `DiffService.BuildRows` does its own order-preserving alignment scored by Sørensen–Dice over
  character bigrams, falling back to positional pairing above a size ceiling.
- **AvalonEdit**, two instances, left read-only and right editable. Monospace, DPI-aware, tab width
  from `.editorconfig`, highlighting via `.xshd`. Change bars and line backgrounds via
  `IBackgroundRenderer` — **never insert a visual element per line.**
- **Opens on the first change, not line 1**, with three lines of context above, and the caret there
  too, since `SelectedRows` reads the caret line.
- **Synchronised scrolling goes through `IScrollInfo`** (`TextView.SetVerticalOffset`), never
  `TextEditor.ScrollToVerticalOffset`, which only queues a scroll for the next arrange pass. Read the
  offset off the `TextView`, not the `TextEditor`. Horizontal offsets clamp to the narrower document,
  and recognising that clamp as an echo rather than a gesture is what keeps the panes from fighting.
- Right-click acts on the row under the pointer, in either pane — Revert, Stage hunk, Unstage hunk. A
  4 px `GridSplitter` between the panes, ratio not persisted. An overview strip down the right edge
  maps the whole file to the pane's height, merged in pixel space with a two-pixel floor.
- `Ctrl+F` opens a find bar in the header stack and is the pane's own key, not a window binding.
- Prefetch the **top 5 files** plus the one under the keyboard cursor, keyed by path and content hash.
  Above **500 KB** disable word-level diff; above **2 MB or 50,000 lines** fall back to a read-only
  unified view and say so.

## Live editing the working tree

**The feature most likely to destroy user work. These rules are not optional.**

On load, detect and store the **encoding including BOM presence** (UTF-8 with and without BOM are
different files to Git), the **dominant line ending**, and **trailing-newline presence**. On save,
rewrite with the same three. Silently normalising line endings on a Windows tool turns a three-line
change into a whole-file diff.

- **Never auto-save.** Explicit `Ctrl+S`. Dirty state in the header, blocking on close.
- Write atomically: temp file in the same directory, then `File.Replace`, preserving attributes.
- **Detect external modification before writing** — size, last-write time and hash stored at load. If
  any changed, do not overwrite: offer reload, overwrite, or save-as.
- After a save, refresh that file's counts and re-run its diff. Not the whole list.
- Read-only for binary files, oversized files and unresolved conflicts; never edit outside the resolved
  repository root; refuse to save into a path that has become a symlink or junction since load.

**Reverting lines** replaces the selection with the left side's version (a caret inside a hunk takes the
whole hunk). It is an **edit, not a Git operation**: nothing is staged, no process runs, nothing reaches
the disk, so `Ctrl+Z` takes it back and `Ctrl+S` is still the only thing that writes — which is why
there is no confirmation. `DiffPane` keeps its own history of file texts, one per structural change,
restored by re-diffing, because `TextEditor.Text`'s setter calls `UndoStack.ClearAll()`. **Do not
replace that with `Document.Replace`:** undoing a rebuild would leave `AlignedDocument`'s anchors
describing the layout just undone, and the next save would write alignment padding into the source.

**The staged-versus-worktree trap.** A file already staged and then edited is edited in the *working
tree*, so the change will not be in the commit even though the diff looks complete. Label the left side
permanently, show an inline restage strip when the user edits a staged file, and on commit restage
every edited file if they chose restage. **Never silently commit a stale staged version of a file the
user just edited.**

**Line and hunk staging** — `Hunks` turns any set of rows into a unified patch and `PatchService`
applies it with `git apply --cached`, which touches the index and never the working tree. The same
function serves whole-hunk and selected-line staging, and the same patch reversed serves unstaging.
`git apply` compares context byte for byte, so every emitted line is re-terminated from the `FileText`
it came from; a file ending with a newline produces one diff row past its last line, which must not be
emitted as context; and an unstaged deletion becomes context while an unstaged insertion vanishes.

## Commit and push

```bash
git -C <repo> add -- <resolved paths>
git -C <repo> commit -F <temp-file>
git -C <repo> push                      # or push -u origin HEAD
```

Stop at the first failure and report the failing step. **Always a temp file for the message**, even for
one line, deleted afterwards including on failure. Do not commit with nothing staged or an empty
message. After success show the short hash, optionally close, optionally offer Push.

Guardrails, checked **before** executing:

- **No upstream:** ask once, remember per repository (`flickgit.allowUpstreamCreation`).
- **Behind the remote:** offer `pull --rebase --autostash` then push as a single button. Do not push and
  let it fail.
- **Diverged, or push would require force: stop.** Never offer force-push from any surface.
- **On the primary branch:** a warning strip if enabled (default on) — the one case where the fast path
  deserves friction.
- Secret detection runs before the commit, not only before the AI call.

**Primary branch resolution**, most specific first: `flickgit.primaryBranch` in the repository's own
config, the user setting, `symbolic-ref refs/remotes/origin/HEAD`, `main`, `master`. Cache only the
answer that costs a ref lookup. Never block a window on it — show the window without the strip.

**Branch choice is an editable ComboBox in the commit window**, not a separate action. Default is the
current branch, and committing without touching it must involve no extra Git work. Free text matching
no ref is a new branch name, and the box shows the resolution inline as the user types. **The order
matters** — staging is index-based and survives a switch:

```text
typed value == current branch   → commit, push
typed value is an existing ref  → switch, refresh, commit, push
typed value is new              → check-ref-format, switch -c, commit, push -u
```

`git switch` carries uncommitted changes across when there is no conflict and refuses when there is.
**If it fails, stop** — do not stash, do not force; report which files block it. After a successful
switch the reviewed diff was computed against the old HEAD, so refresh and recompute before committing,
and abort if any selected file changed as a result.

## Pull --rebase

```bash
git pull --rebase --autostash
git submodule update --init --recursive    # only when .gitmodules exists
```

**`--autostash` is unconditional and there is no second verb without it** — the user reaching for Pull
is usually part-way through something, and it is Git's own flag rather than a stash/pull/pop sequence
of ours precisely because there is then no window in which a stash exists that nothing is tracking. Show
the submodule update as a distinct step; a submodule failure does **not** roll back the pull. On
conflict, show a clear message and **do not automatically abort a rebase**.

## Pull requests

GitHub, GitLab and Azure DevOps, cloud or self-hosted. `PullRequestFlow` in Core owns the order:
**push the branch → find an existing request → create → open it in the browser**.

- **The push is first and it is not optional** — everything after it asks a server about a branch it
  does not yet have. It goes through `PushService`, so a diverged branch is refused here exactly as
  from the commit window, and force-push is reachable from neither.
- **Creating an upstream is consent**, through the same `UpstreamConsent` the commit surface uses.
  Declining stops the flow; it does not fall through to the create.
- **The existing-request check runs before the create.** All three services refuse a duplicate with a
  status code and none says where the existing one is.
- **Credentials:** a token FlickGit stored for this host, then `git credential fill` (with
  `credential.interactive=false`, so a helper with nothing stored answers with nothing), then ask once
  and store it. Tokens are filed per host as `FlickGit:forge:<host>`. A 401 is retried once, and only
  a 401.
- **`ForgeUrl` is the one place here where a wrong answer is expensive** — every other mistake is an
  error message, and this one opens a request against a repository that is not the user's. An
  unrecognised host is **refused rather than guessed at**, naming `flickgit.forge`.
- The target is `flickgit.pullRequestTarget`, then the primary-branch chain, over the branches that
  exist **on the remote**. The remote is the branch's own tracked remote first, then `origin`, and its
  **push URL** when it has a separate one. **Nothing touches the network before the window paints.**
- The three APIs differ in auth (Bearer, Bearer, Basic-with-token-as-password), in the draft flag, in
  the id field (`number`, **`iid` not `id`**, `pullRequestId`) and in whether the web URL is returned
  or has to be built. Azure DevOps `api-version` is pinned to **6.0**, the lowest carrying every field.
  Errors are read with `JsonDocument` because the three disagree about an error's shape.
- **No forks, no reviewers, no labels, no work items, no merging, no approving, no comments.** The
  finished URL is checked for an `http`/`https` scheme before `UseShellExecute`.

The description is **one request**: first line is the title, blank line, then Markdown, so
`PullRequestPrompt.Split` parses it by Git's own rule and the title box fills in first. The commit
subjects come before the diff in the payload, and the diff is read against the **merge base**.

## Log

Commit history, and **the combined diff over a selection** — the reason this window exists:
`git diff <oldest selected>^ <newest selected>`. One command, always fast, that cannot fail.

```text
┌─ Log — d360-portal ──────────────────────────────────────────────────────────────────────┐
│ 400 a1b2c3d  feat: add PgBouncer connection pooling HEAD ▸main Thomas Q. 2026-08-21 14:03│
│ 399 9f0e1d2  fix: pool leak on reconnect                       Thomas Q. 2026-08-21 09:40│
╞═════════════════════════════════════════════════════════════════════════════════════════╡
│ 3 commits · 4d5e6f7^..a1b2c3d   ·   including 1 you did not select                        │
├────────────────────────────────┬─────────────────────────────────────────────────────────┤
│ Changed files                  │ 4d5e6f7^ ↔ a1b2c3d                                       │
├────────────────────────────────┴─────────────────────────────────────────────────────────┤
│ 12 files · +418 −233                  [ Create changelog… ]  [ Save as patch… ]  [ Close ]│
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

**The gap disclosure is a requirement, not a nicety.** A gapped selection — 1, 2 and 5 — diffs `1^..5`,
so the skipped commits are in the diff. The window states it (`including 1 you did not select`,
computed in `CommitRange` where it is tested, never in the header's formatting). **A combined diff that
quietly swept in commits the user did not pick is the one failure this window must not have.**

**The range** is `CommitRange.Resolve`, a pure function in Core, because the list is **newest-first** —
the newest selected commit is the *lowest* index, and "the range came out the wrong way round" is
exactly the bug clicking does not reveal. One commit gives its `Parents[0]`..itself; N commits give the
oldest's `Parents[0]`..newest; a root gives the empty tree `4b825dc…`; a merge gives `Parents[0]` with
no special case. The base is always a **bare object id**, never revision syntax.

Reading history uses `git log --format=%H%x1f%h%x1f%P%x1f%an%x1f%aI%x1f%D%x1f%B%x00` plus
`--name-status -z` and `--numstat -z` per range. `%B` is last and is the only free-text field, and the
split is bounded at the field count. `tformat` appends a newline after every record, *after* our NUL.
`%P` is empty for the root commit, so split without `RemoveEmptyEntries` yields a base spec of `""`.
`--name-status`'s similarity score is glued to the letter (`R100`) and a rename consumes **two** extra
fields. **Paging is `--skip`**, never a last-sha cursor: `<sha>^` does not resolve at a root commit, and
at a merge it silently switches the walk to the first-parent line.

**Nothing in this window writes to the repository.** `HistoryService` reaches Git only through
`ReadAsync`, and a test asserts every invocation is a read. No checkout, reset, revert, cherry-pick,
rebase, amend, tag-at-commit or branch-from-here. Scope is `git log HEAD` with no branch picker.

Two outward actions that touch no repository state: **Save as patch…**
(`git diff --binary --output=<file>` — `--output` so the patch never becomes a C# string and cannot
gain a BOM that would make `git apply` refuse it) and **Create changelog…**, the same range described
for a reader who will never see the code. The changelog repeats the gap disclosure, carries no branch
name and no hashes, chooses Brief or Detailed as the payload's last line rather than in the prompt, and
with no AI configured opens holding the commit subjects as a bulleted list. **No version number, no
date, no `CHANGELOG.md`** — those are writes to the repository.

## Blame

Who last touched each line, and — the reason this exists — **what was there before**, since the commit
a blame names is very often only the last to reformat or move the line.

**Git computes the step, not the window.** `git blame --porcelain [<rev>] -- <path>` emits
`previous <sha> <path>`, so nothing appends `^` or resolves a parent, a rename is followed by using the
path Git reported, and `boundary` ends the walk honestly. `Alt+←` returns, restoring the caret line as
well as the revision.

Porcelain traps: metadata appears once per commit, not per line, so cache by sha and re-attach; the
content line is found by its **leading TAB**, never by exhausting known keys; a sha of forty zeros is
"not committed yet" and still carries a `previous`; `author-time` is epoch seconds with a separate
`author-tz`, kept rather than converted. No `--no-color` (the format has none), `blame.ignoreRevsFile`
deliberately honoured, no `-M`/`-C`. **A binary file is refused by us** — Git blames it into nonsense.

The gutter is a `BlameMargin` on one read-only editor, drawn once per **run** of the same commit.
`BlameService` reaches Git only through `ReadAsync`. No `-L` range, no author filter, no branch picker,
and none of the history-rewriting verbs.

## Branches, tags, stashes, submodules, clone, repository settings

**Branches** — fuzzy filter over local branches with remote-tracking ones below. Attempt a plain switch
first; if Git refuses, **do not stash automatically** — show the blocking files and offer
`[ Stash, switch, restore ]  [ Open commit window ]  [ Cancel ]`, where the stash path restores **only
the stash it created**. Create is the filter box itself, from **HEAD**, after `check-ref-format`. Delete
is a right-click on the row under the pointer, and **`branch -D` is never reached by the window
deciding to reach it**: `branch -d` runs, an unmerged refusal gets its own second question, and only an
answer to *that* forces. **Deleting on a remote is the only thing in FlickGit that destroys state other
people share** — confirmed in its own words, pushing a fully qualified `refs/heads/<branch>` (the bare
name is ambiguous with a tag), with the remote resolved against the configured remotes rather than split
at the first slash. No force, no lease.

**Tags** — what exists, create one on HEAD, delete one (remote first, never forced, no `ls-remote` on
open). Checking one out runs `git switch --detach <tag>` after one question, and **this is the only
thing in FlickGit that detaches HEAD** — everywhere else that state is reported and refused. The window
stays open to say so. No moving a tag, no `--force`, no signing, no tag-at-a-chosen-commit, and no
command-line spelling.

**Stashes** — what is put away, put the working tree away, pop one back, drop one. Untracked files are
a checkbox, ticked by default, and **`--all` is never passed** — that would take ignored files too.

**A stash is named by a position, and that is the whole safety rule here.** `stash@{1}` is whatever is
second at the moment the command runs, and the list is renumbered by any push or pop — a terminal's, an
IDE's, or FlickGit's own stash-switch-restore, while the window sits open. So `GitStash` carries the
stash commit's sha and **every pop and drop re-reads the list and refuses unless the reference still
names that commit**, which is how the rule `SwitchService` keeps by finding its own stash by message is
kept by a window that has to address rows the user points at. Popping the wrong stash is a merge nobody
asked for; dropping the wrong one is somebody's work gone.

Pop asks nothing — it restores work rather than discarding any, and Git refuses rather than overwriting
— and a failed pop **always** leaves the stash in place, because Git applies and only then drops. Drop
asks in its own words: a stash has no reflog, so nothing here finds it again. **No `clear`** (one click
that destroys every saved change), **no `apply`** (a second spelling of pop), no `stash branch`, no
`--keep-index`, no force. Popping and dropping have **no command-line spelling**, because a reflog
selector written into a script is a position that will have moved by the time it runs.

**Submodules** — what is there, add one, remove one, and **it commits nothing**: both operations leave
their work in the index and the window's button opens the commit window. Two reads only:
`config -f .gitmodules --list -z` (the only source that lists an uninitialised submodule) and one
`diff HEAD --name-only -z --ignore-submodules=none`; initialised is `File.Exists(<path>/.git)`. A
submodule's name defaults to its path, so it may contain dots — the name is everything between the
first separator and the last. Removing is `deinit` then `git rm`, in that order, and only a second
answer to Git's own refusal forces. **`.git/modules/<name>` is never deleted** — it can hold commits
made in there and never pushed. Adding refuses before Git runs: no URL, no path, an absolute or
escaping path, a non-empty target, a path already declared.

**Clone** — shown when the folder is not inside a repository, and the default rather than `git init`.
Prefill from the clipboard **only if** it matches a Git remote shape. Always clone into a
**subdirectory** of the clicked folder. `--progress` writes to stderr; parse it for a determinate bar.
**Cancellation kills the process tree and deletes the partial target directory**, and only ever one the
tool created in this operation. **Do not implement credential handling** — show Git's stderr and suggest
`git credential-manager`.

**Repository settings** (`flick repo`) — the identity this repository commits as, its remotes and their
URLs, and FlickGit's four per-repository keys. One `config --local --list -z` gives all of it, so
`git remote -v` is never parsed. `config --list` lower-cases the section and the final component and
leaves the subsection alone, so match keys case-insensitively and remote names never; `config --unset`
exits **5** when the key was not there, which is success here. Remote edits apply per button; the
identity and the defaults apply on Save; a rename and a re-point in one press run **rename first**. No
network, no credentials, no global config, no `git init`.

**Per-repository keys** live in the repository's own `.git/config`, not `settings.json`, so they cannot
go stale when the repository moves and are not committed: `flickgit.primaryBranch`,
`flickgit.allowUpstreamCreation`, `flickgit.pullRequestTarget`, `flickgit.forge`.

## The Explorer trigger

```text
Browsing C:\dev\d360-portal  →  trigger  →  < 120 ms  →  commit window, caret in the message box
                             →  AI message streams in  →  Enter  →  commit + push, toast, close
```

**The default is `Ctrl+Alt+G` through `RegisterHotKey`, and it installs no hook at all.** A global
low-level input hook on a first run by an unsigned binary is what EDR products flag. Two Explorer-scoped
mechanisms (a key via `WH_KEYBOARD_LL`, a mouse side button via `WH_MOUSE_LL`) are specified and **not
built**, and there is **no settings value for them**: `TriggerKind` is `Hotkey` or `None`, because a
setting that silently falls back to something else is worse than one that does not exist.

If a hook is ever built: the proc runs on **every input event system-wide** and Windows silently unhooks
it past `LowLevelHooksTimeout` (300 ms), so it may only compare the input against the configured trigger
and a **cached bool** for whether Explorer is foreground — resolved in a
`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` callback, never by calling `GetForegroundWindow` or
`GetWindowThreadProcessId` in the proc. Cache the verdict, not the pid: Explorer's pid changes on every
restart and is plural. Then `PostMessage` and return; never do Git work there.

**Folder resolution** via `IShellWindows`: the selected item in the active view if it is a folder,
otherwise the current folder of the active window. **There is no third step** — a trigger with no
Explorer folder behind it opens nothing at all, because the rule is never to act on a repository the
user is not looking at. Windows 11 tabs put several tabs behind one HWND, so compare the resolved path
against the address-bar title where possible and log the ambiguity when two tabs are undecidable. Not a
repository → the clone dialog.

**What opens** is the pre-warmed commit window, keeping the position WPF gives it, and it does **not**
close on focus loss — an accidental click outside must not throw away a message being typed.

## Repository palette

Secondary surface for when the user is not in Explorer. Global hotkey **`Ctrl+Alt+R`** — not
`Ctrl+Alt+G`, which the trigger holds, since two `RegisterHotKey` calls for one combination cannot both
succeed. It opens on **repositories that have something to do**, not on a command list.

```text
┌──────────────────────────────────────────────────┐
│ >                                                │
├──────────────────────────────────────────────────┤
│ ● d360-portal        3 modified      ↑2          │
│ ● bookmeta           1 modified                  │
│   oceaview           clean                       │
├──────────────────────────────────────────────────┤
│ ⏎ commit   Ctrl+⏎ pull --rebase all              │
└──────────────────────────────────────────────────┘
```

- **Render from cache synchronously on open**, then refresh asynchronously and update in place. Never
  wait on a `git` process before showing. Cache TTL 5 s, invalidated on any write.
- Scan in parallel, always `--no-optional-locks`. Never enumerate thousands of refs per keystroke.
- Typing filters; a space or `>` switches to action mode. Second-token completion comes from
  `ActionParameter` — `Branch` reads `for-each-ref`, and `Tag` has **no completion source**, because the
  token after `tag` is a tag being *created*.
- Subsequence fuzzy matching (`cnb` → `commit-new-branch`), scored by contiguity, word boundaries and
  MRU rank. The exact command is shown in the footer before Enter. `Esc` closes with no side effects.

Target: hotkey to painted palette **80 ms**.

---

# Action Catalog

The context menu, the palette and the CLI must not each define their own list of operations. Define
actions once, project them into every surface — the Explorer menu (via registry sync), the palette, and
the CLI (`flick <verb>` for a built-in, `flick run <id>` for a user action).

```csharp
public sealed record GitAction
{
    public required string Id { get; init; }          // "commit", "custom.fetch-prune"
    public required string Label { get; init; }       // already localised
    public required ActionRun Run { get; init; }
    public string? IconFileName { get; init; }        // a name inside icons\, never a path
    public ActionSurfaces Surfaces { get; init; }     // [Menu] [File] [Palette]
    public bool RequiresRepository { get; init; }
    public bool RequiresConfirmation { get; init; }   // forced on for anything destructive
    public ActionOutput Output { get; init; }         // toast, window, none
    public ActionParameter Parameter { get; init; }   // none, branch, tag
    public int MenuOrder { get; init; }
    public bool InMoreSubmenu { get; init; }          // one level only, per Windows 11
    public bool Hidden { get; init; }                 // built-ins are hidden, never deleted
    public bool IsBuiltIn { get; init; }
    public string? Cli { get; init; }                 // the verb spelling, for a built-in
}
```

- **One requirement flag, not six.** `RequiresRepository` is the only distinction anything draws. There
  is no `Cli` surface flag either — the command line reaches an action by verb or id, never by asking
  the catalog what to offer.
- **A built-in's id *is* its CLI verb**, which makes `flick commit` and the Commit action one code path.
- `IconFileName` is a bare file name, so a value from `actions.json` cannot name a location outside the
  resolved icons directory.
- **`RequiresConfirmation` travels in one direction only.** `ActionSafety` turns it on for anything on
  the **Safety Rules** list and for every `ProcessRun`; nothing in the file can turn it off. The user
  wrote the file, so they may have the command — but not silently.
- `ActionRun` variants: `WindowRun`, `GitRun`, `ProcessRun`, `CompositeRun` (ordered, stop on first
  failure). Placeholders `{repo}`, `{branch}`, `{upstream}`, `{remote}`, `{selection}`, `{files}` are
  substituted into `ArgumentList` entries — **never** into a concatenated string.
- Built-ins ship in code and can be hidden, relabelled or reordered via `builtIns` in `actions.json`,
  never deleted.
- **Security:** `actions.json` can launch arbitrary processes. The UI must warn clearly when creating a
  `ProcessRun`, and the file must never be importable from a URL or a repository without explicit
  confirmation.

## The default menu projection

```text
… the rest of the Explorer context menu …           A right-clicked FILE instead gets:
─────────────────────────────────────────
Pull (rebase)         ← + submodule update           FlickGit  ▸  Blame…
Commit / Push…        ← branch in the label                      Add
FlickGit            ▸                                            Remove…
      ├── Show log…          ├── Pull request…
      ├── Branches…          ├── Repository settings…
      ├── Tags…              ├── Clone…
      ├── Submodules…        ├── Fetch (prune)
      ├── Stashes…           ├── Open terminal here
      └── Push               ├── Add
                             └── Remove…
```

Two root entries, because those are the two the user *performs* all day. Everything else is one hover
away, and there is **no "More" entry**: the root entries *are* the menu and the submenu *is* the
overflow. On a file the folder entries are absent rather than greyed, and `ActionSurfaces.File` is what
puts an action there.

**Add and Remove** are `TrackingService`, and both answer in text, so the CLI verbs are the same code
path. They are the only entries that act on something smaller than the repository, which is why they
sit last in the submenu and `rm` last of the two. Add stages (nothing to confirm for a file — staging
discards nothing). Remove deletes and stages the deletion, behind one question, under four rules:
**nothing is forced** (so `git rm` itself enforces "never discard uncommitted work"); **an untracked
path is refused before the question**; **it asks on every surface**, with a dialog even from the
command line; and **the pathspec cannot glob** — every command passes `:(literal)<path>`, or
`a[1].txt` would match `a1.txt`.

**On a folder both act on everything below it, and three things pay for that.** First, the surface:
`ActionSurfaces.Folder` is *not* `Menu`, so the entries are drawn on a folder the user pointed at and
never on a folder background, a drive, or the repository root — where Commit is already the entry that
stages everything, and where `flick add .` from a terminal is refused by name. Second, the question
carries the count, read first with `ls-files -z` and `diff --name-only -z`: the number of files is the
one part of the blast radius the user cannot see. Third, `-r` appears on exactly two argument vectors
and never without a second flag disarming it — `--dry-run`, which changes nothing, and `--cached`,
which cannot reach the working tree. A test asserts there is no third.

**A folder removal goes to the Recycle Bin, not to `git rm -r`**, because a folder is exactly where the
untracked files are and Git would refuse over them or leave them behind. That puts the destructive step
outside Git, where Git can no longer refuse it, so `FolderRemovalFlow` collects the refusal in advance:
**gate (`rm -r --dry-run`) → ask → bin → record (`rm -r --cached`)**. Run the gate after the bin and a
folder holding uncommitted work is gone before anything objects, with every step still reporting
success — which is why the sequence is in Core with tests rather than in the verb.

---

# AI

The AI is an accelerator, never a dependency. Three surfaces — a commit message, a pull-request
description, a changelog — over one interface:

```csharp
public interface IAiGenerator
{
    IAsyncEnumerable<string> GenerateAsync(AiPrompt prompt, CancellationToken cancellationToken);
}
```

`AiPrompt` is a system prompt, a payload and a token ceiling. The return type is a stream, not a
`Task<string>` — streaming is a requirement, not an option. `AnthropicGenerator`, `OpenAiGenerator`,
`CopilotGenerator`, `OllamaGenerator` and `DisabledAiGenerator`, with **no base class**: what they share
is one function they all call (`AiEndpoint.StreamAsync`) and what differs is exactly its arguments.
`AiTextService` owns the streaming state machine and one failure counter across all three surfaces.

| Provider | Model | Notes |
|---|---|---|
| **Anthropic** (default) | `claude-haiku-4-5-20251001` | extended thinking **not** enabled |
| **OpenAI** | `gpt-5.6-luna` | `reasoning: { "effort": "none" }` |
| **GitHub Copilot** | `gpt-4.1` | undocumented API; see below |
| **Ollama** (local) | `aiModel`, **no default** | nothing leaves the machine |

The task is short-output summarisation and does not benefit from reasoning: pick the fastest tier and
turn reasoning off. `max_tokens` is 150 for a commit subject and 700 for a description. **Cost is not a
constraint** (~$1/month at ten commits a day on Haiku) — design around **time to first token**.

**Copilot** is the only provider whose stored credential is not what gets sent: the GitHub OAuth token
buys a short-lived token from `copilot_internal/v2/token`, cached until two minutes before expiry.
`Copilot-Integration-Id: vscode-chat` and `Editor-Version` are required or the endpoint answers 400 with
an empty body. An empty `choices` array is the ordinary first frame, not a fault. **A personal access
token does not work at all**, fine-grained or not — the prompt and the 404 both say so, and name
`%LOCALAPPDATA%\github-copilot\apps.json`. GitHub may change or close this without notice, which is why
Anthropic is the default.

**Ollama** is the only provider available under a policy that forbids source code reaching a third
party, needs no credential, and carries **no `Authorization` header at all**. It has **no default
model** — any guess 404s, so an empty `aiModel` is refused by name pointing at `ollama list`. It speaks
newline-delimited JSON rather than SSE, and its error is a bare string. Its silence budget is **two
minutes**, because a cold model is reading gigabytes off disk, and its warm-up *loads the model* rather
than opening a socket. `aiOllamaUrl` is a setting because running the model on another machine on the
network is the ordinary reason to use it — the one case where "local" stops being literally true, so
`flick ai` says which of the two it is.

## Latency

**1. Cap the diff — the biggest lever.** Under 12 KB, send it verbatim; above, send a file summary
**synthesised from the `--numstat -z` counts already in hand** (never `git diff --stat`, which is
human-readable), the first 40 lines of each file's hunks, and a `[truncated]` marker per file. Hard
ceiling **4,000 input tokens**, applied *after* the per-file cap.

**2. Stream**, and render tokens as they arrive; with queued Enter the practical wait is zero.

**3. Keep the connection warm** — a cold TLS handshake is 100–300 ms, a third of the budget:

```csharp
new SocketsHttpHandler
{
    PooledConnectionLifetime       = TimeSpan.FromMinutes(15),
    PooledConnectionIdleTimeout    = TimeSpan.FromMinutes(10),
    EnableMultipleHttp2Connections = true,
}
```

One warm-up request at service start; do not poll. Copilot's warm-up must exchange its token too — that
is ~450 ms that would otherwise land inside the first generation.

**4. Constrain the output.** `max_tokens` is a runaway guard; the real control is the system prompt.
Strip code fences defensively anyway. Prompt caching does not pay for itself here.

## The prompt

Three files in `%LOCALAPPDATA%\FlickGit\`, seeded by `PromptStore` on first run and owned by the user
thereafter: `commit-prompt.md`, `pull-request-prompt.md`, `changelog-prompt.md`. What the file says is
sent **verbatim** (HTML comments stripped, so the seeded header can explain itself without reaching the
model). A file with no prompt left in it is **refused rather than sent** — an empty system prompt does
not fail, it produces confident nonsense. Deleting one resets it: the seed runs on every launch. Read on
**every generation** rather than cached, because iterating on wording is the point.

The built-in commit wording, which is the fallback:

```text
Given the following Git diff, produce a concise commit message.

Rules:
- summarise the intent, not every changed line
- first line <= 72 characters when practical
- imperative mood
- do not invent changes
- output only the commit message
- use Conventional Commits when clearly appropriate
```

`aiConventionalCommits` is not consulted while a file exists — a file is the whole prompt, and appending
a rule the user did not write is the surprise this removes.

**The payload is not templatable, and that is the boundary.** `AiContext.ToPromptText` and `DiffPayload`
decide what may leave the machine, so a prompt file changes the instructions and can never widen them.

**Not `git diff --cached`** — `CommitFlow` stages as its *first* step, so at message time the index is
usually empty. Use `git diff HEAD -M --no-color --no-ext-diff --no-textconv -- <ticked paths>`, which is
what the commit will actually contain. Staging early to make `--cached` true is the wrong fix: Esc would
then leave the index mutated.

## Privacy and failure

**A provider with a key stored for it is the consent.** There is no second switch: the one thing an AI
provider does here is read a diff. The provider is named in Settings, `flick ai` states outright that
the diff of the files being committed is sent to it, and choosing nobody sends nothing.

Run the secret detector **before sending and before committing** — AWS keys, GitHub tokens, generic API
keys, private key blocks, connection strings, passwords. Never send `.env`, credentials or private keys.
Always exclude lock files, generated code and minified assets. On detection, warn and redact.

On failure — unreachable, invalid key, rate limited, or **8 s of silence** — the message field becomes an
ordinary editable box with a one-line notice, and commit and push stay fully available. **The 8 s
measures silence, not total duration**: every frame restarts it, so it guards a request that has stopped
answering rather than truncating a healthy generation, and a stall raises `AiUnavailableException`
naming the provider rather than looking like the user pressing Esc. Log the reason; **never log the diff
or the key.** Three consecutive failures raise a persistent tray warning.

**Speculative generation** is specified, opt-in, off by default and **not built**. It must be
automatically disabled when the provider is not local, and it would save a wait queued Enter already
hides.

---

# Settings, Text and Persistence

Every string the windows show comes from one `key = value` file per language, embedded in
`FlickGit.exe`: `src/FlickGit.App/Languages/{en,de,es,fr,it,pt}.lang`. Not `.resx` — satellite
assemblies are per-culture DLLs, and a plain text file is something a translator can send back as a
diff.

- `en.lang` is the master and the **per-key** fallback, so a half-finished translation shows English
  rather than raw key names. `@name` is the language's own name for itself, never translated.
- Adding a language is adding a file: the csproj embeds `Languages/*.lang` by wildcard and `Strings`
  enumerates manifest resources, so no surface can offer a language the exe was not built with.
- **`WithCulture=false` on the `EmbeddedResource` item is load-bearing** — without it MSBuild builds a
  satellite assembly that is never found at run time.
- **`Strings.Use` must be called before the first window is constructed**, since views read their text
  on construction and the resident service keeps them for the session. Changing language says to
  restart. An unknown code is refused with exit code 4 and the list.
- **One word per meaning:** `common.close` for a button that only dismisses a window, `common.cancel`
  only for one that actually stops something.

The settings window (`flick settings`) is three tabs and deliberately small: the Explorer menu on/off,
the repository overlay on/off, start with Windows, three commit switches, the pull switch, the AI
provider and its key button, the language picker, and a pointer to `settings.json` / `actions.json`. **It is not a full settings app and
will not be** — a drag-and-drop action list with icon pickers would be more UI than the rest of the
product, and a graphical front end for a documented, hand-editable file. What earns a window is the
switches whose JSON key nobody can guess, plus the AI key, which lives in Credential Manager and is in
no file at all.

- **Every value is read from its source of truth on open** — the registry, the Task Scheduler. Never
  from a remembered flag: a checkbox disagreeing with the registry is worse than no checkbox.
- **Nothing is applied until Save**, and Save touches the registry or the Task Scheduler only when the
  answer changed. **The API key is the one exception** and applies immediately.
- Constructed on demand, not pre-warmed. The one window with no latency target.
- **Help** renders `Help.md` (shipped beside the exe) read-only, through our own ~300-line renderer,
  because the dependency list is fixed at three. **About** carries the version and
  <https://github.com/o0Zz/FlickGit/>.

```text
%LOCALAPPDATA%\FlickGit\settings.json     schemaVersion (3) + general settings
%LOCALAPPDATA%\FlickGit\actions.json      user actions + built-in overrides
%LOCALAPPDATA%\FlickGit\*-prompt.md       commit, pull-request, changelog
%LOCALAPPDATA%\FlickGit\icons\            user-supplied .ico files
%LOCALAPPDATA%\FlickGit\Logs\
```

Both JSON files carry `schemaVersion`; an unknown future version is **refused with a clear message
rather than silently migrated**. Writes are atomic: temp file, then `File.Replace`. **Nothing
per-repository lives in `settings.json`** — those are `flickgit.*` keys in the repository's own config.
**API keys are never written to these files**: Windows Credential Manager (`CredentialStore`, keyed by a
target string) or DPAPI only.

**Registry synchronisation** on save: compute the desired state from the Action Catalog, delete only
keys under FlickGit-owned paths, write the new keys, then **verify by reading back** and report failures
in the UI.

---

# Installer

`src/FlickGit.Setup` builds `FlickGit-<version>-x64.msi`, and it exists for **one file**:
`FlickGit.Shell.dll` is locked by `explorer.exe` from the first right-click, so replacing it is a
*sequence* — the one thing an archive cannot be.

```text
close FlickGit → close Explorer → replace files → register → start Explorer → start FlickGit
```

**Per-user, no elevation**, into `%LOCALAPPDATA%\Programs\FlickGit`, with `HKCU` keys and a per-user
logon task. One `UpgradeCode` forever, plus `MajorUpgrade` and `AllowSameVersionUpgrades`.

**Registration goes through `flick.exe`, not MSI registry rows** — the menu depends on the user's
`actions.json`, so `install-shell`, `uninstall-shell` and `autostart on|off` are custom actions and
there is exactly one piece of code that writes those keys.

The sequence *is* the package. Each of these is a bug if got wrong, and each is commented in the
`.wxs`: the kills are **immediate and before `InstallValidate`**; `MSIRESTARTMANAGERCONTROL` is
**deliberately not set** (`Disable` means the pre-Vista code path — measured 4m 49s against 10s);
`MSIFASTINSTALL` is **7**, whose bit 2 skips the free-space walk that costs an SMB timeout per
unreachable mapped drive; `install-shell` is **deferred**, since an immediate action after
`InstallFiles` still runs before any file exists; **every deferred action is `Impersonate="yes"`** or it
runs as SYSTEM and registers the menu into the wrong hive; the starts are immediate via `Start-Process`,
because MSI waits for an exe action to exit; **FlickGit starts five seconds after Explorer**, because
`Shell_NotifyIcon` fails while the notification area does not exist; nothing runs twice during an
upgrade (`NOT UPGRADINGPRODUCTCODE`); the kills use `SystemFolder` as their working directory, since
`INSTALLFOLDER` may not exist; and **`install-shell` is the only action allowed to fail the install.**

**Three version fields in the package, four on the file** — Windows Installer compares only
`major.minor.build`, so the `ProductVersion` is `build.yml`'s `msiversion`: the one version number with
its last field dropped. Two builds off one tag therefore share it, which is what
`AllowSameVersionUpgrades` is for. The `.msi`'s *name* carries all four fields
(`PackageDisplayVersion`), so it and the portable zip are visibly the same build. Four ICEs are suppressed and each is argued with in the csproj
(ICE38/64/91 object to a per-user install; ICE61 fires because of `AllowSameVersionUpgrades`); nothing
else is, and ICE03 has already caught a real bug. The .NET Desktop Runtime is checked with a **directory
probe**, because the .NET installer records versions as registry *value names*.

Verified by running it, not by a test — read the verbose log (`msiexec /i … /l*v`). The runs worth
doing: a first install, an upgrade over a version whose DLL Explorer has loaded, an uninstall (which
must leave `%LOCALAPPDATA%\FlickGit` alone), and both silently with `/qn`.

---

# Safety Rules

Never automatically execute:

```bash
git reset --hard      git clean -fd       git clean -fdx      git checkout -- .
git restore .         git branch -D       git push --force
```

Any destructive operation requires **explicit user intent, expressed in the moment**. Never discard
uncommitted work.

The hotkey trigger, the palette and the command line are **not** shortcuts around these rules. Actions
marked `RequiresConfirmation`, and every operation above, require a second explicit confirmation
regardless of surface. **Force-push is never offered from any surface.**

The one place that asks Git to discard uncommitted work is **Revert file**, which names a single path
after `--` and sends the copy on disk to the Recycle Bin first.

---

# Error Handling, Notifications, Logging

Never swallow Git errors. Display the operation, the Git error, the repository path, and a suggested
next action.

```text
Rebase stopped because of conflicts.

Resolve the conflicts, stage the files, then continue with:

git rebase --continue
```

```text
Unable to switch to main because local changes would be overwritten.

No files were modified or discarded.
```

Never show generic errors such as "Something went wrong." When an operation fails midway, preserve
repository state and explain what happened.

**Notifications** are native Windows notifications or small non-intrusive dialogs. Avoid unnecessary
confirmation dialogs; optimise for one-click workflows.

**Logging** under `%LOCALAPPDATA%\FlickGit\Logs\`, with rotation: Git command name, duration, exit code,
repository path, sanitised errors, and the latency measurements. **Never log** API keys, credentials,
diff contents, file contents or commit message bodies.

---

# Performance Targets

Every one of these must be measurable and surfaced by `flick diag timings`.

| Path                                       | Target | Hard limit |
|--------------------------------------------|--------|------------|
| CLI stub start → exit                      | 30 ms  | 80 ms      |
| Trigger → commit window painted            | 120 ms | 250 ms     |
| Palette painted after hotkey               | 80 ms  | 150 ms     |
| Commit window visible (service warm)       | 120 ms | 250 ms     |
| Commit window visible (cold fallback)      | 900 ms | 1500 ms    |
| Status + numstat merge                     | 60 ms  | 150 ms     |
| Click → rendered diff (prefetched)         | 80 ms  | 200 ms     |
| Click → rendered diff (cold)               | 250 ms | 600 ms     |
| Re-diff after edit, 2,000-line file        | 120 ms | 300 ms     |
| AI first token (Haiku 4.5, capped diff)    | 400 ms | 1.5 s      |
| AI complete message                        | 800 ms | 3 s        |
| AI request timeout (silence, not total)    | —      | 8 s        |
| AI description / changelog first token     | 600 ms | 2 s        |
| Log window painted, first 200 commits      | 250 ms | 600 ms     |
| Commit selection settled → file list       | 150 ms | 400 ms     |
| Blame painted, 2,000-line file             | 250 ms | 600 ms     |
| Blame previous revision, one step          | 200 ms | 500 ms     |
| Pull request window painted                | 250 ms | 600 ms     |
| Pull request plan settled → summary        | 200 ms | 500 ms     |
| Commit + push, warm, excluding network     | 400 ms | 1 s        |
| Shell handler state / title                | 20 ms  | 50 ms      |
| Overlay `IsMemberOf`, per drawn item       | 0.2 ms | 1 ms *(see below)* |
| Resident idle working set                  | 80 MB  | 150 MB     |
| Input hook proc                            | < 1 ms | 5 ms *(see below)* |

**Overlay `IsMemberOf`** is the second exception to "surfaced by `flick diag timings`", and for a
structural reason rather than a cost one: `OperationTimings` lives in `FlickGit.Core`, and this code
runs inside `explorer.exe`, where that assembly may not go. It is verified by scrolling a large folder
and a mapped network drive, which is where a regression would show first.

The **input hook proc** is the other exception, and it has to be:
`OperationTimings.Record` allocates, and an allocation inside the proc risks a GC pause inside the very
300 ms budget whose overrun silently unhooks the feature. Two `static long` counters (last input tick,
inputs seen) reported by `flick diag doctor` are the honest substitute.

Explorer integration must never block on network operations. Never perform `git fetch`, `git pull`, AI
requests or large diff parsing while Explorer is building a context menu.

If the median AI first token exceeds 1 s in real use, **the diff cap is too high** — check that before
blaming the provider. Suggest `core.fsmonitor` in `diag doctor` for large repositories, where it takes
`git status` from ~300 ms to a few milliseconds on Windows.

---

# Coding Guidelines

Prefer:

- Small focused classes
- Constructor dependency injection, typed, with no `IServiceProvider` anywhere but the composition root
  — see **Hard Requirement 3**
- `async`/`await` end to end
- Cancellation tokens on everything that touches a process, the network or the file system
- Immutable result models
- Nullable reference types enabled, warnings as errors
- Strict separation between UI and Git logic — `FlickGit.Core` references no UI assembly

Avoid:

- Static global state, and any static that performs I/O
- Service location: `GetRequiredService` outside `App.xaml.cs`
- Shell command string concatenation
- Blocking `.Result` or `.Wait()`
- Business logic in WPF code-behind, or inside any shell extension
- Parsing human-readable Git output
- Abstractions with a single implementation
- Compatibility shims, `[Obsolete]` members and migration code — see **Hard Requirements**

---

# Definition of Done

A feature is complete only if:

- It works from Explorer
- It works from the CLI
- It works with the resident service stopped
- Git errors are shown clearly, with the repository path and a next action
- It never discards user changes
- It never rewrites a file's encoding or line endings
- Paths containing spaces work
- Unicode paths work
- Cancellation behaves safely
- The UI remains responsive
- Its performance target is measured, not assumed
- Important logic has tests, within the scope of **Hard Requirement 4**
