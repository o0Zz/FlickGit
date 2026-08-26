# CLAUDE.md — FlickGit

## Project Overview

Build a native Windows Git productivity tool in C# that integrates directly into Windows
Explorer.

The goal is extremely fast Git workflows without requiring TortoiseGit, Fork, GitKraken or
another full Git client.

The target user works across 5–10 repositories per day and switches between them
constantly. The bottleneck being solved is **not** the commit dialog — it is the cost of
getting to the right repository and back out again. Every design decision follows from
that.

Core capabilities:

- Git actions from the Explorer context menu
- A global hotkey that opens the commit window on the folder Explorer is showing
- A commit window with a TortoiseGit-style file list (status, lines added, lines removed)
- A fast side-by-side diff viewer with a **live-editable** right pane
- AI-generated commit messages
- Branch creation, quick pull/rebase workflows, push
- Clear, non-destructive error handling

Optimise for speed, clarity, safety and minimal UI — in that order, and never at the
expense of safety.

---

# Product Philosophy

This tool is not intended to replace a complete Git client.

> Make the most common safe Git operations available in zero or one clicks, from wherever
> the user already is.

Favour speed, clarity, safety, minimal UI and predictable Git behaviour over covering every
Git feature.

Whenever a workflow becomes complicated, expose the repository state clearly instead of
being clever or silently modifying it.

---

# Hard Requirements

Four rules that override convenience, habit, and anything elsewhere in this document that reads
like a suggestion.

## 1. Break things freely

**There is no backwards compatibility in this project. None. Not with a previous version, not
with a config file an earlier build wrote, not with anything FlickGit has ever shipped.** This is
not a tolerance for breaking changes — it is a requirement to make them whenever the new design is
better, and it outranks every instinct that says otherwise. Do not ask whether a change is
breaking. Ask only whether it is right, and then make it.

When a better design appears, take it and change whatever it touches:

- **Settings, `actions.json`, cached state** — change the shape, drop keys, rename them. Bump
  `schemaVersion` and let an old file be refused. **Never write a migration, a converter, an
  upgrade path or a default that quietly stands in for a removed key.** A user whose config is
  refused edits four lines or deletes the file; that is cheaper than the code that would have
  spared them.
- **Registry layout, CLI verbs, exit codes, action ids, IPC messages, pipe names, file layout** —
  rename or remove them outright. No aliases, no shims, no fallbacks, no deprecated spelling that
  still happens to work, no "accept both for now".
- **Types in `FlickGit.Core`** — there is no external consumer. Change the signature rather than
  adding an overload beside it. Rename the type rather than keeping the old name pointing at it.
- **Delete rather than deprecate.** No `[Obsolete]`, no "legacy" path kept alive, no second code
  branch preserved for the way it used to work, no commented-out previous version. When something
  is replaced, the old thing goes in the same change.
- **An old install is not a constraint.** Reinstalling, re-registering the menu, re-entering an
  API key or deleting `%LOCALAPPDATA%\FlickGit` are all acceptable costs of an upgrade, and none of
  them justifies a line of compatibility code.

This is a per-user tool with one install, no plugin API and no other consumers. A migration path
costs real complexity to serve a user who does not exist.

**Do not propose compatibility as a courtesy.** Do not flag a change as breaking, do not offer to
keep the old spelling working, do not leave the previous behaviour reachable behind a setting.
Answering "this would be a breaking change" is not a reason to hesitate here; it is a description
of the normal case.

**What this does not license.** Breaking changes are about *our* formats and *our* interfaces —
never about the user's data. Everything under **Safety Rules** holds unconditionally: no
destructive Git operation without explicit intent, never discard uncommitted work, never rewrite
a file's encoding or line endings. "Compatibility does not matter" is not "the working tree does
not matter."

## 2. Build only what is asked for

Solve the problem in front of you, at the size it actually is.

- **No abstraction until there is a second caller.** One implementation needs no interface, no
  factory and no registry. Add the seam when the second thing arrives, not in anticipation of it.
- **No setting nobody asked for.** A named constant with a comment explaining the number beats a
  config key, a settings row and a persisted field.
- **No generality for its own sake.** Two similar cases stay two straightforward cases until a
  third makes the pattern real.
- **Prefer the boring mechanism.** A method call over an event, a field over a state machine, a
  `switch` over a strategy hierarchy.
- **Do not split a class that is easier to read whole.**

The exception is the code that touches the user's working tree. Diff reconstruction, encoding and
line-ending preservation, staging, and the safety guards earn their explicit named steps and their
tests — there, "simple" means *legible and verifiable*, not *short*. Everywhere else, the shortest
thing that works and reads clearly is the correct thing.

## 3. Everything by constructor injection

A class receives what it needs as constructor parameters, typed. Not a container, not a static, not
something it reaches out and fetches for itself.

- **Never inject `IServiceProvider`.** A class that takes the container has no declared
  dependencies, so nothing can be substituted and nothing can be read off its signature. If a
  constructor would need eight services, that is a class doing two jobs — split it, do not hand it
  the container.
- **Never `new` a collaborator that does I/O.** Processes, files, the registry, the network, the
  clock. Those arrive through the constructor so a test can pass something else. Value objects,
  view models and windows are not collaborators; `new` those freely.
- **Statics that *do* something become services.** Reading the registry, shelling out to
  `schtasks`, loading or writing a file, probing for a pipe: injected instances with real types.
  Three kinds of static stay: one that merely *names* a location (a settings path, a log directory,
  a pipe name), a pure function of its arguments (a parser, a matcher, a validator), and the thinnest
  possible wrapper over a process-global OS facility that has exactly one implementation forever —
  the console, the clipboard. The distinction is behaviour, not syntax.
- **Per-invocation state is a parameter, not a field.** Where the answer goes, which repository was
  clicked, what the user typed: passed in per call. That is what lets the services using it be
  singletons with nothing to reset between uses.
- **Register in one place.** `App.xaml.cs` is the only file that mentions the container. Everything
  else declares its dependencies and knows nothing about how it was built.

## 4. Test the core, not everything

The suite covers `FlickGit.Core` and the handful of behaviours the product would be dangerous or
useless without. It does not chase coverage.

**In scope, and only this:**

- **Parsers, and the pure functions beside them.** `--porcelain=v2 -z`, `--numstat -z`,
  `--name-status -z` and the `git log` format — the places where a wrong byte becomes a wrong file
  list — and `CommitRange.Resolve`, where a wrong index becomes a diff of the wrong commits.
- **The commit sequence.** `CommitFlow` — stage, switch, verify, commit, push, in that order.
- **The safety rules.** A blocked switch changes nothing; a stash restores only the one it created;
  a diverged push is refused; `add -A` never appears in an argument list; `branch -D` never appears
  unless force was asked for and the current branch costs no Git call at all; untracked and
  secret-matching files are not staged by default.
- **The working tree.** Encoding, BOM and line-ending round trips, and the one value that may ever
  be written to a file. The "legible and verifiable" exception above lives here.
- **The command-line grammar**, because every surface goes through it.

**Out of scope. Do not add tests for these:**

- **Anything in `FlickGit.App`.** No view models, no windows, no XAML, no WPF test project. A test
  that has to construct a `Window` is testing WPF, and the resident service is verified by running
  it. This is why `Verb` lives in `FlickGit.Core`: the grammar is worth testing, the window that
  shows the result is not.
- **Plumbing.** IPC framing, the tray icon, the registry writer, logging, notifications, autostart.
  Exercised end to end by hand when the feature is built, where a failure is immediately visible.
- **Secondary features.** Clone, fuzzy matching, the diff renderers, repository detection.
- **A real `git.exe`.** The fake runner makes the *arguments* assertable, which is the half that
  matters; a temporary repository hides them and costs seconds per test. Real Git is exercised by
  running the product.
- **A second test per rule.** One test per behaviour. If two tests always fail together, one of
  them is not earning its place.

A new test needs a sentence saying which of the five in-scope bullets it belongs to. If there is no
such sentence, the answer is not to write it.

---

# Technical Direction

Use:

- C#
- .NET 9 or newer
- WPF for UI
- Native AOT for the CLI stub
- Git CLI as the source of truth — call the installed `git.exe`, never reimplement Git
- Async process execution throughout
- Native Windows shell integration

Supported Git installations: Git for Windows, or a portable Git configured manually.

**Minimum Git 2.23** (August 2019), which is what introduced `switch` and `restore`. Those
spellings are used unconditionally and there is no fallback to `checkout` or `reset` — see
**Hard Requirements**. An older Git fails with its own "'switch' is not a git command",
which is a clearer diagnosis than anything a shim would produce.

## Why not libgit2

The alternative is LibGit2Sharp, and it looks like it would be faster and less code. It is
neither, and this is the record of the evaluation so it is not proposed again.

**It would be a second binding, not a replacement.** Six things the product already does have
no LibGit2Sharp equivalent, so `git.exe` stays and the binding is added beside it:

| Operation | libgit2 |
|---|---|
| `pull --rebase --autostash` | no autostash. The stash/rebase/pop sequence becomes ours — the exact thing **Pull --rebase** refuses, because there would then be a window in which a stash exists that nothing is tracking. |
| `apply --cached`, for hunk staging | `GIT_APPLY_LOCATION_INDEX` exists in libgit2 and is unbound in LibGit2Sharp. |
| credential helpers | libgit2 never runs `credential.helper`. GCM, Azure DevOps and PAT prompting would all become ours, against **Clone**'s "do not implement credential handling". |
| `git@host:path` remotes | the shipped native binaries are built without libssh2. SSH remotes do not work at all. |
| `blame --porcelain`'s `previous` | not exposed — and that walk back is the reason the Blame window exists. |
| `submodule update --init --recursive` | a thin binding, not an equivalent. |

Three more are quieter and worse. **libgit2 runs no hooks**, so `pre-commit` and `commit-msg`
silently stop firing and FlickGit commits what the user's terminal would have rejected. **It runs
no `filter.*` process filters**, so an LFS file reads back as its pointer text — which the diff
pane would show and a save could write over the real file. And **it does not understand the sparse
index or reftable**; writing an index it misread is the one failure **Safety Rules** does not
survive.

**The speed argument measures the wrong thing.** In-process saves some 15–25 ms of Windows process
creation per call, which is real — and then the three status commands run in parallel, so the
commit window pays one launch of wall-clock against a 60 ms budget. Latency here was bought by the
resident service paying WPF startup once at login, not by the Git binding. On a large repository
libgit2's status is *slower* than Git's: no `core.fsmonitor`, no untracked cache, which is the
case `diag doctor` recommends fsmonitor for.

**And the parsers are the asset, not the cost.** Porcelain v2, numstat, name-status, the log format
and blame porcelain are the tested part of the product precisely because faking
`IGitProcessRunner` makes the *arguments* assertable with no repository on disk — that `add -A`
never appears, that every read carries `--no-optional-locks`, that `HistoryService` only ever
reads. A typed object model moves that surface behind a P/Invoke boundary a unit test cannot
substitute. Two diff engines that can disagree about rename detection or line endings is a bug
generator, not a simplification.

A read-only status, log and blame viewer with no writes, no network and no hooks would be a good
fit for it. This is not that.

Third-party dependencies, all MIT:

| Purpose              | Library                       |
|----------------------|-------------------------------|
| Text editor control  | AvalonEdit (ICSharpCode)      |
| Line/word diff       | DiffPlex                      |
| Tray icon            | H.NotifyIcon (avoids WinForms)|

Do not add an Electron-style or web-based UI layer. Do not add a general-purpose DI
container heavy enough to affect startup; a minimal `Microsoft.Extensions.DependencyInjection`
container is fine.

---

# Architecture

Business logic never runs inside `explorer.exe`. Every shell surface is a thin trigger that
launches the CLI stub, which forwards to the resident service.

```text
FlickGit.sln

src/
├── Shared/                  Compiled into *both* executables by <Compile Include>.
│   └── IpcMessages.cs       The pipe's wire format. Shared as source rather than as
│                            a third assembly, because the AOT stub must not carry a
│                            reference it would have to load.
│
├── FlickGit.Cli/            Native AOT. No WPF, no WinForms.
│   └── Parses args, connects to the named pipe, exits.
│       Falls back to launching FlickGit.App directly.
│
├── FlickGit.App/            WPF. Resident, single instance, tray icon.
│   ├── App.xaml.cs          Composition root and process lifecycle. Nothing else.
│   ├── CommandLine/         RepositoryVerbs answer in text about a repository,
│   │                        EnvironmentVerbs about the installation, WindowVerbs
│   │                        open something and stay. VerbRunner routes; VerbOutput
│   │                        decides between the console and a window, and is passed
│   │                        per call rather than injected. ActionRunner executes a
│   │                        catalog action, and lives here because that is the same
│   │                        job as a verb.
│   ├── Views/               Windows, the diff pane, and PopupPlacement.
│   ├── ViewModels/          Presentation state. No Git logic. CommitViewModel is the
│   │                        whole of the commit surface -- there is only one.
│   ├── Rendering/           Diff renderers, gutters, DiffBrushes, and
│   │                        AlignedDocument -- the only thing that converts between
│   │                        the padded editor document and the file on disk.
│   ├── Resident/            Pipe server, tray, notifier, and the window hosts.
│   │                        AppWindow is the pre-warm, which only these two
│   │                        pay for, and the show sequence, which every window
│   │                        in the product goes through.
│   ├── Trigger/             The global hotkey and Explorer folder resolution.
│   ├── Ai/                  AiTextService: the failure counter and the streaming
│   │                        state machine, for both the commit message and the
│   │                        pull-request description. Here rather than in Core
│   │                        because it reads settings and the credential store.
│   ├── Shell/               Registry projection of the Action Catalog.
│   └── Settings/ Tray/ Localization/ Infrastructure/ Languages/
│
├── FlickGit.Core/           No UI dependencies. The only tested assembly.
│   ├── Cli/                 Verb, VerbKind, ExitCodes -- the command-line grammar.
│   │                        Here rather than in the WPF assembly so it is testable
│   │                        without a message pump.
│   ├── Git/                 GitProcessRunner, GitExecutable, errors
│   ├── Repositories/        RepositoryService
│   ├── Status/              porcelain v2, numstat and name-status parsing,
│   │                        StatusService, StatusComparer
│   ├── Diff/                DiffService, FileTextLoader, WorkingTreeWriter,
│   │                        DiffDocument, and Hunks + PatchService -- the patch
│   │                        generator and `git apply --cached`
│   ├── Files/               FileTrackingService -- `git add` and `git rm` on the one
│   │                        file the Explorer file menu was opened on. Neither is ever
│   │                        forced, recursive, or a pathspec that can glob.
│   ├── Commits/             CommitService, and CommitFlow -- the stage/switch/
│   │                        verify/commit/push sequence
│   ├── Blame/               BlameService and BlamePorcelainParser -- the annotation,
│   │                        and Git's own `previous` that walks it back
│   ├── History/             HistoryService, CommitLogParser and CommitRange --
│   │                        the read-only log, and the oldest^..newest rule the
│   │                        combined diff is
│   ├── Actions/             ActionCatalog and its data: GitAction, ActionRun,
│   │                        ActionSafety, ActionPlaceholders, actions.json
│   ├── Palette/             RepositoryScanner and the cached overview the palette
│   │                        paints from before Git is asked anything
│   ├── Ai/                  What may leave the machine (DiffPayload, AiContextBuilder --
│   │                        one builder, because the two surfaces differ only in their
│   │                        revisions) and the four providers. AiEndpoint is the request
│   │                        they all make, and IAiGenerator takes a prompt rather than a
│   │                        commit -- which is what lets a second surface use them.
│   │                        PromptStore is the other half of that: the system prompt as
│   │                        a file the user owns, with CommitPrompt and
│   │                        PullRequestPrompt left as the built-in it falls back to.
│   ├── Forges/              Pull requests, on GitHub, GitLab and Azure DevOps.
│   │                        ForgeUrl is the parser; PullRequestService assembles the
│   │                        plan; PullRequestFlow is the push-then-create order;
│   │                        three clients over one ForgeApi. GitCredentialFill is
│   │                        how a token is found without asking.
│   ├── Branches/            BranchService, SwitchService
│   ├── Config/              RepositoryConfigService -- the identity, the
│   │                        remotes and the four flickgit.* keys, out of
│   │                        one `config --local --list -z`
│   ├── Remotes/             PushService, and RemoteService -- adding,
│   │                        renaming, re-pointing and removing one
│   └── Pulls/ Clone/ Secrets/ Matching/ Logging/ Diagnostics/ Models/
│
└── FlickGit.Shell/          Native AOT COM DLL, loaded into explorer.exe. Draws the
    │                        whole FlickGit block: the branch in the Commit label,
    │                        repository-requiring items omitted outside a repository,
    │                        and a separator either side. Hand-rolled vtables, no
    │                        [GeneratedComInterface] -- see Com.cs. No ProjectReference.
    ├── Exports.cs              DllGetClassObject, DllCanUnloadNow, IClassFactory.
    ├── ContextMenuHandler.cs   The one COM object: IContextMenu + IShellExtInit.
    ├── Selection.cs            The clicked folder, from a PIDL or a CF_HDROP.
    ├── MenuRegistry.cs         The menu, as the App wrote it into the CLSID key:
    │                           flick.exe's path, the submenu's label and icon, and
    │                           every item. One key, so one class and one read.
    ├── MenuIcons.cs            An .ico as a 32bpp menu bitmap. InsertMenu takes text
    │                           only, so the alpha has to be drawn by hand.
    ├── GitHead.cs              The branch, from .git/HEAD. No git.exe, no pipe.
    └── RepositoryLookup.cs     One answer per right-click instead of four.

    This does NOT reach the Windows 11 primary menu -- that still needs a sparse MSIX
    package and package identity, which is the part of Phase 6 still open. A
    ContextMenuHandler is honoured in the classic menu with an ordinary per-user COM
    registration, which is what this uses.

src/FlickGit.Setup/          WiX -> FlickGit-<version>-x64.msi. A per-user install that
                             closes FlickGit and Explorer, replaces the files, registers
                             the menu and the logon task, and starts both again -- which
                             is the only order in which a DLL loaded into explorer.exe can
                             be replaced. Not in FlickGit.sln: it packages publish output
                             rather than compiling sources, and runs the three publishes
                             itself. See Installer.

tests/
└── FlickGit.Core.Tests/     The only test project, and there will not be a second
    └── one -- see Hard Requirement 4. Parsers, the commit sequence, the safety
        rules, the working tree, the command-line grammar. Fakes the process runner
        rather than starting git.exe, so the *arguments* are assertable.
```

**Sequences belong in Core, not in a view model.** Anything with an order that matters — stage
then switch then verify then commit, plan then consent then push — goes in `FlickGit.Core` and gets
tests. A WPF view model can only be exercised by clicking, and "the steps happened in the wrong
order" is exactly the bug clicking does not reveal. View models own presentation: what the list
shows, what the hint says, which words describe an outcome.

Assembly names differ from project names on purpose:

| Project        | Output         | Why |
|----------------|----------------|-----|
| `FlickGit.Cli` | `flick.exe`    | The command the user types, and the command written into every registry verb. |
| `FlickGit.App` | `FlickGit.exe` | The resident process. Its first icon is the context menu's root icon. |
| `FlickGit.Core`| `FlickGit.Core.dll` | `net9.0`, not `net9.0-windows`: nothing in it touches a Windows API, which makes the no-UI rule structural rather than a review convention. |

Both executables must sit in the same directory. `flick.exe` resolves `FlickGit.exe` beside
itself, and the registry command lines name `flick.exe` and `icons\*.ico` by path -- a publish
layout that nested or renamed either one installs a context menu whose entries do nothing.

## Process split rationale

`flick.exe` **must** remain Native AOT. A framework-dependent .NET stub costs
50–100 ms of CLR startup on its own, which defeats the purpose of the resident service.
Target: process start to exit under **30 ms**.

`FlickGit.App.exe` pays WPF startup once, at login, and keeps pre-warmed windows alive.

Shell entries launch the stub with arguments:

```text
flick.exe clone "C:\dev"
flick.exe commit "C:\Projects\MyRepo"
flick.exe pull-rebase "C:\Projects\MyRepo"
flick.exe switch "C:\Projects\MyRepo"
```

---

# Command Line Interface

The same actions must be reachable from Explorer, a terminal, scripts, keyboard launchers,
PowerToys Run and future integrations.

```text
flick clone <path> [url]             clone into a subdirectory of <path>
flick commit <path>                  commit window (branch ComboBox included)
flick pull-rebase <path>             --autostash, + submodule update when applicable
flick push <path>
flick pr <path>                      open a pull request for this branch
flick switch <path> [branch]         branch picker when omitted
flick tag <path> [name]              tag window when omitted; creates and pushes it when named
flick submodule <path>               submodules: add, remove, initialise
flick status <path>
flick log <path>                     commit history; multi-select for a combined diff
flick blame <file>                   who last touched each line, and what came before
flick add <file>                     stage one file, tracking it if it is new
flick rm <file>                      delete one file and stage the deletion; asks first
flick repo <path>                    identity, remotes and this repository's defaults
flick run <id> [path]                run a catalog action by id
flick palette                        global repository palette
flick settings
flick install-shell                  register context menu entries
flick uninstall-shell
flick autostart [on|off]             logon task for the resident service
flick ai                             what the AI is configured to do
flick ai key [set|clear]             store or remove the API key
flick language [code|auto]           interface language; lists them when omitted
flick diag timings                   recent latency measurements
flick diag doctor                    environment and integration health check
```

`<path>` defaults to the current working directory when omitted.

Exit codes: `0` success, `1` Git error, `2` not a repository, `3` user cancelled,
`4` configuration error, `5` operation refused for safety (blocked switch, diverged push).

---

# Repository Detection

Before displaying or executing any Git action, determine whether the path belongs to a
repository:

```bash
git -C "<path>" rev-parse --show-toplevel
```

Must support right-clicking:

- the repository root
- a subdirectory inside the repository
- the Explorer background while browsing inside a repository

Normalise everything to the repository root. If the path is not inside a repository, fail
gracefully — a one-line message, never a full window.

Cache resolved roots in the resident service with a short TTL (30 s), keyed by directory.
Invalidate on any write operation the tool performs.

---

# Git Command Execution

A single reusable async process runner. Every Git call in the product goes through it.

```csharp
Task<GitResult> RunAsync(
    string repositoryPath,
    IReadOnlyList<string> args,
    CancellationToken cancellationToken);

public sealed record GitResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration);
```

Requirements:

- Always `git -C "<repository>" ...`
- **Always `--no-optional-locks` for read operations.** Without it, `git status` refreshes
  and writes the index. When the tool scans repositories in the background while an IDE is
  doing the same, this produces `index.lock` contention. This is not theoretical at 5–10
  repositories.
- `ProcessStartInfo.ArgumentList` only. **Never build a command string.** No shell
  interpolation anywhere in the codebase.
- No visible console window (`CreateNoWindow`, `UseShellExecute = false`)
- UTF-8 for stdout and stderr; set `core.quotepath=false` via `-c` so non-ASCII paths come
  back unescaped
- Capture stdout, stderr and exit code separately
- Full cancellation support, including killing the process tree on cancel
- Read stdout and stderr concurrently — never sequentially, or large diffs deadlock on a
  full pipe buffer

Locate `git.exe` in this order: user setting, `PATH`, standard install locations. Cache the
result. Surface a clear error if not found rather than failing per-command.

---

# Commit Window

The commit window. **The only commit surface**, reached from the context menu, from the global
hotkey, and from the CLI.

There used to be a quick-commit popup as well — a smaller surface with no file list, for the fast
path — and this window was "the escape hatch" behind it. It was removed: two surfaces meant two
places for every commit behaviour to live, and the popup's own section admitted it had "no file
list, which leaves three specified behaviours without a home". What made the popup fast was not its
size. It was the caret already sitting in the message box, Enter committing, and the AI message
streaming in while the user decided. Those moved here, so this window opens the way the popup did
and shows everything the popup could not.

```text
┌────────────────────────────────────────────────────────────────────────────┐
│ d360-portal                              feature/storage-gw    ↑2 ↓0       │
├──────────────────────────────────┬─────────────────────────────────────────┤
│ Changed files                    │ Working tree ↔ HEAD                     │
│                                  │                                         │
│ ☑ M src/GatewayClient.cs +42 -17 │  41  services.AddSingleton  │ 41  ...   │
│ ☑ M src/Options.cs        +8  -2 │  42                          │ 42  ...   │
│ ☑ A src/PgBouncerPool.cs +156 -0 │  43- var pool = new Pool();  │ 43+ var…  │
│ ☑ D src/LegacyPool.cs     +0 -203│  44  return pool;            │ 44  ...   │
│ ☐ ? scratch/dump.json    +12  -0 │                                         │
│ ☑ M assets/logo.png       bin    │  ← read-only        editable →          │
├──────────────────────────────────┴─────────────────────────────────────────┤
│ Commit message                                                             │
│ feat: add PgBouncer connection pooling to the storage gateway              │
│                                                       [ Generate with AI ] │
├────────────────────────────────────────────────────────────────────────────┤
│ [ Commit & Push ]   [ Commit ]                              [ Close ]      │
└────────────────────────────────────────────────────────────────────────────┘
```

The window instance is pre-warmed by the resident service and reused. It must be fully
re-initialisable; no state may leak between two uses. This is the main correctness risk of reuse,
and every mutable field is assigned in one method — `CommitViewModel.Reset` — so that adding a field
has one place to look. Not a test: **Hard Requirement 4** puts everything in `FlickGit.App` out of
scope, and it overrides anything here that reads like a suggestion. Verified by running it.

`CommitContext` in this document means two different things: the input to that reset, and the input
to an AI generator. Only the second is a type.

## Keyboard

The caret is in the message box from the moment the window is populated, and stays the thing the
window is arranged around.

```text
⏎          commit & push
⇧⏎         newline, for a multi-line body
Ctrl+⏎     commit & push, from anywhere including the file list
Ctrl+S     save the diff pane's edit
Ctrl+Z     undo the diff pane's last change
Ctrl+F     find in the diff pane, in whichever side has the caret
F3 / ⇧F3   the next and previous match, with the caret back in the pane
F5         re-read the status, the same as the Refresh button
esc        close the search bar if it is open, otherwise close the window
```

`F5` is a window binding rather than a button accelerator, so it works from the diff pane and the
file list as well as the message box — and it goes through the view model's own command, so it obeys
the same "not while busy" rule the button does instead of stacking refreshes on a slow repository.

**Enter also accepts the two confirmations this window raises** — Revert and Delete — where every
other guardrail dialog in the product leaves Enter on the negative. That is `ConfirmWindow`'s
`defaultIsAffirmative`, and the two questions that pass it are exactly the two that send the copy on
disk to the **Recycle Bin** before anything else happens. The recoverability is what earns them one
question rather than two in the first place; it is the same thing that makes Enter a safe answer, and
without it a mass revert costs a mouse trip to a dialog per file. The guardrails that keep Enter
meaning "no" are the ones with no undo: publishing a branch, pulling before a push, overwriting a
file that changed under the editor, deleting a branch on a remote.

**Enter commits rather than inserting a newline**, which is the one thing about this window nobody
can guess — so the footer says so whenever there is no outcome to report in its place.

**Except while the diff pane has keyboard focus.** Its right-hand pane is an editor over the user's
working tree, where Enter is a newline in their file. Committing instead would be surprising and
unrecoverable in the same keystroke, so the Enter rows above are suspended there — and the pane's own
keys, `Ctrl+F` and `F3` among them, are the ones that keep working.

**Esc closes, always — including from inside the diff pane, and including while the AI is still
writing.** One key, one outcome. It briefly cancelled a running generation instead and left the
window open, needing a second press; since generation starts on every open, that made Esc look
broken for the first half-second of the window's life. Closing cancels the generation on the way
out, and a queued Enter cannot fire into a window that is gone.

**Unless the search bar is open**, where Esc closes that instead. This is the one thing allowed to
stand in front of the rule above, and it is bounded by the fact that the user opened the bar with
`Ctrl+F` a moment earlier: dismissing the thing you just summoned is the only meaning Esc has while
it is showing, and the alternative is throwing away a half-typed commit message to close a search
box. One press, then another, and both do what the surface in front of the user says. Because this
window intercepts Esc before the pane ever sees it, it has to *ask* — `DiffPane.CloseSearch` returns
false when there was no bar, and only then does the window spend the key on itself.

The single exception is a commit **already executing**, where Esc does nothing: there is no point of
return that would not leave the repository half-changed, and the window has to stay to report the
outcome. Esc is therefore intercepted rather than left to the Cancel button's `IsCancel`, which
would close the window straight through that guard.

---

# File List

## Data sources

`git status --porcelain=v2` gives status but **not** line counts. Counts come from
`--numstat`. Three commands, run **in parallel**:

```bash
git -C <repo> --no-optional-locks status --porcelain=v2 -z
git -C <repo> --no-optional-locks diff --numstat -z            # worktree vs index
git -C <repo> --no-optional-locks diff --cached --numstat -z   # index vs HEAD
```

Merge on path. Budget warm: **60 ms**.

Never parse human-readable `git status` output.

## Parsing traps

- **`-z` changes the numstat rename format.** A rename emits old and new paths as two
  separate NUL-terminated fields after the counts, not the `old => new` arrow form. Parse
  the NUL stream as a state machine, not line by line.
- **Binary files report `-` for both counts**, not `0`. Display `bin`, never `+0 -0`, and
  never attempt a text diff.
- **Untracked files appear in `status` but not in `numstat`.** Count their lines from disk,
  with a size guard (skip above 1 MB, show byte size instead) and a binary sniff on the
  first 8 KB.
- A file can be both staged and modified in the worktree. Keep the counts separate
  internally, display the sum, show the split in the tooltip.
- Paths may contain any byte except NUL. Never split on spaces.

## Model

```csharp
public sealed class GitFileChange
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }

    public GitChangeType IndexStatus { get; init; }
    public GitChangeType WorkTreeStatus { get; init; }

    public int? AddedLines { get; init; }      // null = binary
    public int? RemovedLines { get; init; }
    public bool IsBinary { get; init; }
    public bool IsUntracked { get; init; }

    public bool IsStaged { get; set; }
    public bool IsSelected { get; set; }
}
```

Support: modified, added, deleted, renamed, copied, untracked, conflicted.

Sort order: conflicted, modified, added, deleted, renamed, untracked last.

---

# Staging Defaults

The user may commit without reviewing the list — the caret starts in the message box, so Enter is
reachable without ever looking at the files. The defaults must be safe without being annoying.

- **Tracked modified and deleted files: checked by default.**
- **Untracked files: unchecked by default**, count shown. This is the rule that prevents
  `.env`, `appsettings.Development.json`, `bin/`, `obj/` and stray dumps from being pushed
  in a hurry. It is the single most valuable safety default in the product.
- Files matching secret-detection patterns are unchecked and flagged in red, even if
  tracked.
- If the resulting set is empty, say so and disable Commit.

Commands:

```bash
git add -- "<file>"                    # stage
git restore --staged -- "<file>"       # unstage
```

**Never run `git add -A` or `git add .`** anywhere in the product. Stage the explicit
resolved path list. The user's selection determines what is committed.

## A deleted file is two states, and one of them must not be staged

Both show a `D` on the row, and `git add` behaves oppositely on them:

| porcelain v2 | meaning | `git add -- <path>` |
|---|---|---|
| `1 .D` | gone from the working tree, index entry still there | stages the deletion |
| `1 D.` | deleted with `git rm`, so the deletion is already staged | **`fatal: pathspec … did not match any files`** |

Pathspec matching looks at the working tree *and* the index. A `git rm`-ed file is in neither, so the
command does not quietly do nothing — it aborts, and with it the whole commit.

So `GitFileChange.IsDeletionStaged` keeps those paths out of `SelectedPaths`. They need nothing doing:
the index already holds exactly what the user is asking to commit. Unticking one still unstages it
normally, because `git restore --staged` matches against HEAD rather than against a pathspec.

This is the second entry in a list that now has three staging states — whole file, chosen hunks,
already-staged deletion — and all three are "leave the index alone" for different reasons.

## Deleting, from the list

Select one row or several, right-click, **Delete file…**, confirm. Every file goes to the **Recycle
Bin**, and no Git command runs at all.

The Recycle Bin is the whole design, not a nicety. **Safety Rules** forbids discarding uncommitted
work, and an untracked file is uncommitted work Git has never seen — `git restore` cannot bring it
back, because there is nothing to restore it from. A shell delete makes a destructive operation
recoverable by a gesture the user already knows, which is what earns it a single question instead of
a warning nobody could act on. It is the same argument **Reverting lines** makes: the safety comes
from the operation being undoable, not from a second dialog. It is also what **Reverting** below
borrows, which is why the two items sit on one menu — and what lets Enter answer the question, per
the keyboard map above.

Because nothing is staged, the two cases resolve themselves and the confirmation says which is
which:

- a **tracked** file becomes an ordinary `D` row the user can tick and commit, or put back with
  `git restore`
- an **untracked** file simply stops existing, and the Recycle Bin is the only way back

A row whose file is already gone — `.D` or a `git rm`-ed `D.` — is **filtered out of the selection
rather than refused later**. The letter on the row already says the answer, and over a selection of
ten it is the only honest way to count what the click will do: the menu item reads *Delete 4 files…*
for a five-row selection holding one of those, and goes dead only when it would touch nothing at all.

Two guards, both refusals rather than best efforts, and the first is Core's own rather than a second
copy of it: **nothing outside the resolved repository root is deleted**
(`WorkingTreeWriter.ResolveInsideRepository`, which is why that is public), and **a symlink or
junction is refused** — deleting through one is how a single click removes a tree that lives
somewhere else entirely.

`WorkingTreeDeleter` is in `FlickGit.App` rather than in Core, and that is forced: it reaches a
Windows shell facility, and Core is `net9.0` precisely so that it cannot. What is in Core is the
part worth testing, which was already there.

**It takes one path, and the caller loops.** Deleting five files is five calls, in list order, and
the first refusal stops it and says how many went before — which is what the Recycle Bin's own
prompt, cancellable per file, forces anyway.

## Reverting, from the list

Select one row or several, right-click, **Revert file…**, confirm, and each file goes back the way
HEAD has it — the working tree **and** the index:

```bash
git restore --source=HEAD --staged --worktree -- "<file>"
```

`--source=HEAD` rather than the default, which restores the working tree from the *index* and would
leave a staged change standing: a file the user had already `git add`-ed would come back looking
reverted and still be committed. "Revert this file" has one meaning, and it is the one the row's
letter goes away for.

**This is the only place in the product that asks Git to discard uncommitted work**, so both halves
of **Safety Rules** meet here. The forbidden spellings — `git restore .`, `git checkout -- .` — are
forbidden because they take the *whole* working tree unasked; this one names the single path the
user right-clicked, after a confirmation, which is the "explicit user intent, expressed in the
moment" the same section allows. It names one path per call and the caller loops, which is what keeps
that true of a selection of forty. The other half, *never discard uncommitted work*, is kept the way
**Deleting** keeps it: **the copy on disk goes to the Recycle Bin first**, and only then does
Git overwrite it. Bin first and restore second, never the reverse — a locked file fails the bin,
and failing there means nothing has happened yet.

**Per file, not per selection**, and that is the reason `RestoreService.RevertAsync` still refuses to
take a list. Binning all forty and then restoring all forty would leave forty files binned and none
replaced when the restore fails; interleaving leaves one. The first failure stops the loop, names the
file in Git's own words, says the version is in the Recycle Bin, and says how many files were
reverted before it.

**A file HEAD does not have is skipped, not refused** — filtered out of the selection the same way
Delete filters a row whose file is already gone. `Ctrl+A` over a list holding one untracked file is
the ordinary way to reach a mass revert, so the menu item names the count it would actually touch and
the confirmation says how many rows it will leave exactly as they are. Every exclusion is that one
question — is this path in HEAD — and one of them is not merely useless:

| Row | Why not |
|---|---|
| untracked `?` | HEAD has nothing to put back. **Delete** is the item for this row. |
| added `A` | Staged, still absent from HEAD — and `restore --source=HEAD --staged --worktree` on such a path **deletes the file, exit code 0, no message**. Uncommitted work destroyed by a command reporting success. Unticking the row is how a staged new file leaves the commit. |
| renamed `R`, copied `C` | HEAD has the *old* path. One command on the new one is the `A` case again; doing it properly is two operations with two ways to fail half way. |
| conflicted `U` | Taking HEAD's side is a merge decision wearing a revert's label, and conflict resolution is out of scope. |

What is left is every ordinary case: modified, deleted, type-changed, staged or not.

`RestoreService` is in Core with the predicate beside the command, because "which rows may be
handed to this" is exactly the kind of rule a click cannot be trusted to reveal. The confirmation
adds a line when the diff pane holds an unsaved edit: that edit was never written to disk, so it is
not what the Recycle Bin receives, and a dialog promising recoverability has to be right about what
it is promising.

## What a multi-selection does to the diff pane

**Nothing, and that is the point.** The file list is `SelectionMode="Extended"` — the log window's
gesture set, for the log window's reason: under `Multiple` a bare click toggles, and a bare click has
to mean "just this one". With two or more rows highlighted there is no single file the right-hand pane
could be about, so it goes back to its prompt.

`SelectedItem` stays bound as the **anchor** row, which is what a diff, a restage strip and a save are
each about. `SelectedItems` is not bindable, so the rest of the selection reaches the view model from
the list's `SelectionChanged` — and `SetSelectedFiles` is the one path that loads a diff, because the
decision depends on a count the `SelectedFile` setter cannot see.

**Except while the pane holds an unsaved edit, where the pane is left alone.** Clearing it goes
through `DiffPane.Show`, which drops `IsDirty` and the undo history with it — so a `Ctrl+click` would
silently discard a working-tree edit, which is the one thing **Definition of Done** makes
unconditional. The edit stays on screen, and the revert confirmation still says the Recycle Bin will
not have it.

**Right-click follows the diff pane's rule**: a click inside the selection means the selection,
anywhere else means the row under the pointer. `FilterList.SelectRowUnderPointer` owns it, and under
`Extended` it has to — a bare `IsSelected = true` there *adds* the clicked row to whatever was
highlighted, which is how a right-click on one file reverts four others.

---

# Diff Viewer

## The constraint that decides the architecture

The right pane is editable. The moment the user types a character, any hunk list produced by
`git diff` is stale. **`git diff` output cannot be the rendering source.**

The viewer diffs two in-memory buffers and recomputes on edit:

```text
LEFT  (read-only)                RIGHT (editable)
base content                     working-tree file
   │                                 │
   │  git show <ref>:<path>          │  File.ReadAllText
   ▼                                 ▼
┌──────────────────────────────────────────┐
│      in-process line diff (DiffPlex)     │  ← re-runs on edit, debounced 200 ms
└──────────────────────────────────────────┘
                    │
                    ▼
         aligned line pairs → renderers
```

The left-hand base is **always HEAD**:

```bash
git -C <repo> show HEAD:<path>    # Working tree ↔ HEAD
```

For an untracked file, the left side is empty.

**There used to be a second comparison, the index — `git show :<path>` — and it is gone.** It was
never reachable: `DiffComparisonMode` had the value, `DiffService` threaded a `mode` parameter
through four methods to honour it, and the diff pane had a branch to label it, but no surface ever
selected anything but HEAD. So it was a branch that could not be taken, and per **Hard
Requirement 1** it was deleted rather than kept behind a flag — the enum, the parameter, the three
branches on it and the `diff.mode.index` string in all six language files.

What that section below is really about survives intact, and does not depend on the mode existing:
the header still states what the left side is, the log window's range label is still what proves a
historical diff cannot be mislabelled, and the restage strip is still what tells the user their
edit is not in the commit. If the index comparison is ever wanted, it is a new feature with a
control to select it — not a parameter waiting for a caller.

Use **DiffPlex** for the line diff, with a word-level pass inside changed line pairs for
character diffs. Do not write a Myers implementation.

**But not DiffPlex's side-by-side alignment.** `SideBySideDiffBuilder` pairs a change block's
deletions with its insertions *positionally* — first with first, second with second, until one side
runs out — and when the two counts differ that is the wrong correspondence. One line replaced with
two insertions above it in the same block pairs the deleted line with the first insertion, so the
red and the green of a plain replacement land on **different rows**, and the word-level highlighting
inside that pair is the difference between two unrelated lines.

`DiffService.BuildRows` therefore takes the line diff and does its own pairing: an order-preserving
best alignment inside each block, scored by the Sørensen–Dice coefficient over the two lines'
character bigrams, plus a constant that breaks a tie towards pairing so a one-for-one replacement of
two dissimilar lines still shares a row rather than becoming a red row stacked above a green one.
Bigrams rather than anything anchored to position, because the question is "is this the same line,
edited" and an edit shifts everything after it. A block big enough that the O(D×I) comparison is not
free is a rewrite, in which the correspondence between one old line and one new line means nothing
anyway — so above a ceiling it falls back to the positional pairing, and says so in a comment rather
than pretending.

## Editor component

**AvalonEdit**, two instances, left read-only and right editable.

- **Opens on the first change, not at line 1.** A file whose first change is three hundred lines
  down otherwise opens on a screenful of unchanged text, and the user has to hunt for the thing they
  clicked the file to see. Three lines of context above it, matching what a unified hunk carries. The
  caret goes there too — `SelectedRows` reads the caret line, so leaving it on line 1 would make
  Stage hunk and Revert lines act on something off screen.
- **Synchronised scrolling locked to the diff alignment, not to raw line numbers — and pushed
  through `IScrollInfo`, not through `TextEditor.ScrollToVerticalOffset`.** That one choice is what
  makes the second pane move in the same frame as the first, and it is what the two traps below
  reduce to.

  - **`ScrollToVerticalOffset` does not scroll.** It asks the `ScrollViewer` to move during the next
    arrange pass. So the target landed a frame late, and the target's own `ScrollOffsetChanged`
    arrived *after* the sync method had returned — meaning the echo could not be recognised by
    anything as cheap as a flag cleared in a `finally`, and it scrolled the source back to where the
    target had just been put. That was worked around by lowering the guard at
    `DispatcherPriority.Background` and parking any gesture that arrived while it was up for a later
    replay. It was correct and it was **visibly laggy**: under a continuous wheel spin the target
    updated once per Background dispatch, starved by the very rendering the scrolling was causing.

    `TextView` implements `IScrollInfo`, and `SetVerticalOffset` is what the `ScrollViewer` itself
    calls. It moves the view *synchronously* — the offset changes and `ScrollOffsetChanged` is
    raised before the call returns. So the echo lands inside the `try`, a single field catches it,
    and the deferral, the parked gesture and the replay are all deleted rather than tuned.
  - **The two panes do not have the same maximum horizontal offset**, because they do not have the
    same longest line — so the offset *clamps* to the narrower document. That clamped value comes
    back as a scroll event, and treating it as a gesture drags the pane the user actually scrolled
    back to wherever the other one could reach. Recognising the echo is the whole fix, and the
    synchronous push is what makes recognising it a one-line reference comparison.

  **Read the offset off the `TextView`, never off the `TextEditor`.** `TextEditor.VerticalOffset`
  reads the `ScrollViewer`, which has not yet caught up with its own `IScrollInfo` child when the
  event fires — so it reports where the source was one notch ago, and syncing to it reintroduces
  exactly the lag this replaced.
- Change bars and line backgrounds via `IBackgroundRenderer` — never insert a visual element
  per line
- **Right-click acts on the row under the pointer, in either pane.** Revert, Stage hunk and
  Unstage hunk, the same three the editing bar carries and against the same rows. It is on the
  **left** pane that it earns its place: the left pane is where the change being undone is *shown*,
  and reaching it otherwise meant finding the same row in the right pane and going up to a button.
  The left document being read-only is no obstacle — reverting writes to the right pane and staging
  writes to the index, so neither touches the side the click came from. A click inside the selection
  means the selection; anywhere else means the line under the pointer, and a single row expands to
  its whole hunk exactly as the caret does. One `ContextMenu` for the two editors, because two would
  be two places for "the same items on the same rows" to stop being true — and no menu at all when
  the pane is read-only, where three items that all refuse say less than nothing.
- **The panes are separated by a 4 px `GridSplitter`, and the split is draggable.** It was a fixed
  50/50 with a one-pixel rule, which left a long line in the right-hand version reachable only by
  scrolling sideways or resizing the whole window — neither of which takes the space from the pane
  that has it to spare. Dragging widens one pane by narrowing the other, which is the same control
  and the same markup as the three splitters already in the commit and log windows, and it is the
  rule between the panes rather than something drawn beside one.

  **The ratio is not persisted and there is no setting for it.** The resident service keeps one
  pre-warmed `DiffPane`, so a drag survives switching files and reopening the window for the rest of
  the session; a `settings.json` key would be **Hard Requirement 2**'s setting nobody asked for.

  This is *not* the 14 px connector strip that used to sit here, one visual tying each changed block
  to its counterpart. That went: at that width it read as a colour belonging to one of the editors
  rather than as a link between them, and it only ever showed the runs already on screen — which is
  the question the overview strip below answers better, for the whole file at once. Per **Hard
  Requirement 1** the class and its three brushes were deleted rather than hidden behind a zero
  width, and a splitter is a control the user acts on, not a decoration between the panes.
- **An overview strip down the right-hand edge**, mapping the whole file to the pane's height:
  a green mark per insertion, red per deletion, blue per modified line, so the changes further
  down the file are visible without scrolling to find them. One strip for both panes, because
  the two documents are aligned row for row and a second would be a copy — and no viewport
  marker, because it sits immediately beside the right editor's own scrollbar and the thumb
  already says where the view is. Marks are merged in *pixel* space, with a two-pixel floor so
  a single changed line in a long file does not round away to nothing.
- Monospace, DPI-aware; tab width from the file's `.editorconfig` when present
- Syntax highlighting via AvalonEdit `.xshd` definitions

## Finding a word

`Ctrl+F` opens a bar under the header. Type, and the pane scrolls to the first match at or after the
caret; `Enter` walks forward and `Shift+Enter` back, wrapping in both directions, with `F3` and
`Shift+F3` doing the same once the caret is back in the pane. `Esc` closes it. Every other occurrence
is tinted amber, which is the half a plain "find next" does not give: where the rest of them are.

- **It searches the pane the caret was in**, left or right, and says which in the bar. The two
  documents are aligned row for row, so the same word in the other pane looks exactly like a match —
  and the left pane being read-only is no obstacle, because searching reads. Only the searched pane is
  lit, or the count would be a claim about a list the highlights disagree with.
- **A row in the header stack, not a panel floating over an editor.** The placeholder that covers the
  panes for a binary or oversized file spans the whole editor grid, so anything placed in it
  disappears behind the one state where the bar must not be showing anyway.
- **`Ctrl+F` is the pane's own key, not a window binding**, for the reason `Ctrl+Z` is: a window
  binding fires on the bubble wherever focus is, so it would open a search bar over a pane the user is
  not looking at from a keystroke in the message box.
- **The current match is shown by selecting it**, so AvalonEdit's own selection marks it and there is
  one colour to keep legible instead of two. One consequence worth knowing rather than preventing: a
  selection in the right pane is what Stage hunk and Revert lines act on, exactly as it is when the
  user makes the same selection by hand.
- **AvalonEdit's own `SearchPanel` was not used.** Its default template inherits the application's
  implicit `Button` style — 88 px wide — so its prev/next/close buttons would each be as long as the
  search box, and escaping that means re-templating a third-party control or fencing it off with a
  resource guard. Finding matches is an `IndexOf` loop and a `BackgroundGeometryBuilder`, which is
  what `Rendering/` is already made of, and it keeps the bar's words in the `.lang` files with every
  other string in the product. No regex, no whole-word, no match-case: three controls on a bar whose
  whole value is that it needs no reading.
- **The highlights are recomputed after every document rebuild, and nothing else moves.** A rebuild
  lands 200 ms after a keystroke in the right pane, and every recorded offset is into the document it
  replaced — but selecting a match then would drag the caret out from under what the user is typing.
  A file change keeps the term and drops the position, which is how one word is chased through
  several files.

## Performance

- Prefetch diffs for the **top 5 files** as soon as the status resolves, into a cache keyed
  by path plus content hash. A click on a prefetched file is a cache hit.
- Prefetch the file under the keyboard cursor as the user arrows through the list.
- Above **500 KB**: keep side-by-side, disable word-level character diff.
- Above **2 MB or 50,000 lines**: fall back to a read-only unified view and say so in the
  header. Do not attempt live re-diff at that size.
- Re-diff on edit is debounced 200 ms, runs off the UI thread, and is cancelled and
  restarted on each keystroke.
- The viewer must remain responsive while a large diff loads. Render progressively.

Targets: click to rendered diff **80 ms** cached, **250 ms** cold. Re-diff after an edit
**120 ms** on a 2,000-line file.

---

# Live Editing the Working Tree

This is the feature most likely to destroy user work. These rules are not optional.

## Preserve the file exactly

On load, detect and store:

- **Encoding**, including BOM presence. UTF-8 with BOM and UTF-8 without BOM are different
  files to Git.
- **Dominant line ending** (CRLF / LF / mixed)
- Trailing-newline presence

On save, rewrite with the **same** encoding, BOM state and line endings. Silently
normalising line endings on a Windows tool turns a three-line change into a whole-file diff,
and it will happen on the first CRLF repository otherwise.

## Reverting lines

The right pane is editable, so the other half of editing is putting something back. **Select lines
and press `Revert lines`, or right-click a change in either pane, and the left side's version of
those lines replaces them.** Landing the caret anywhere inside a hunk without selecting takes the
whole hunk, the same rule hunk staging already uses.

One rule makes this safe enough to offer on a single click, for an operation that otherwise reads as
"discard my work":

> **It is an edit, not a Git operation.** The reverted text goes into the editor exactly as if it had
> been typed there. Nothing is staged, no process runs, and nothing reaches the disk — so `Ctrl+Z`
> takes it back, and `Ctrl+S` is still the only thing that writes. **Safety Rules** forbids
> discarding uncommitted work; until the user saves, none has been discarded.

That is also why there is no confirmation dialog. A confirmation would be friction protecting
against something that has not happened yet, and the thing it would guard — the save — already has
its own explicit keystroke.

**`Ctrl+Z` is the pane's own, and it has to be.** That sentence above was false for as long as this
feature existed: a revert rebuilds both documents, a rebuild assigns the editor's text, and
AvalonEdit's `TextEditor.Text` setter calls `UndoStack.ClearAll()` — so the key reached the editor and
found an empty stack. The same wipe happens on any keystroke that changes the line layout.

The one-line fix is the wrong one and must not be taken. Replacing the assignment with
`Document.Replace` would make the rebuild undoable, and undoing it would restore the previous
*document* text while `AlignedDocument`'s anchors describe the layout that was just undone — so
`ToFileText` would strip the wrong blank lines, and the next `Ctrl+S` would write alignment padding
into the user's source. Keeping the editor's history across a rebuild means custom
`IUndoableOperation`s either side of the replace to restore the anchor set in both directions, which
is subtle bookkeeping in the one place that cannot afford it.

So `DiffPane` keeps a history of **file texts** instead, one per structural change it makes — a
revert, or a layout-changing edit — and a step is restored by re-diffing it, which is the path a
revert already takes: with the base text, a file text determines the rows and the filler layout
outright. AvalonEdit still owns undo for typing inside a line, and the two cannot come out of order
because every snapshot is followed by the rebuild that ends the editor's history. Clearing that stack
is therefore load-bearing rather than incidental, and `BuildDocuments` does it by name.

Two consequences worth stating:

- **It works on a dirty document**, unlike hunk staging, which refuses one. Staging has to describe
  the file to Git in bytes and so needs the document to match what is on disk; reverting only has to
  produce new text. This is why the selection is mapped against the *live* row list rather than the
  one the diff was first computed with — after an edit the viewer re-diffs, and reverting against a
  stale alignment would rewrite lines the user has since changed.
- **It says nothing about line endings**, where `Hunks.ToPatch` has to re-terminate every line from
  the original bytes. The reconstruction works in the normalised `\n` text the viewer holds, and
  `WorkingTreeWriter` restores the file's own encoding, BOM and endings on save. Two places deciding
  line endings is exactly how a one-line revert becomes a whole-file diff.

Reverting an **inserted** line removes it; reverting a **deleted** line brings it back. Both fall
out of one rule — a selected row contributes its left side, and a side that is filler contributes no
line — which is why one function serves both directions and they cannot disagree.

## Save semantics

- **Never auto-save.** Explicit `Ctrl+S`. Dirty state shown in the header, blocking on close.
- Write atomically: temp file in the same directory, then `File.Replace`, preserving
  attributes. This keeps file identity stable for IDE watchers and build tools.
- **Detect external modification before writing.** Store size, last-write time and content
  hash at load. If any changed — the IDE reformatted on save, a build regenerated the file —
  do not overwrite. Offer reload, overwrite, or save-as.
- After a successful save, refresh that file's counts and re-run its diff. Do not refresh the
  whole status list.

## The staged-versus-worktree trap

A file that is **already staged** and then edited in the right pane is edited in the **working
tree**, not in the index — so the change will not be in the commit, even though the left pane is
showing HEAD and the diff on screen looks complete.

**The half of this that came from a second comparison mode is gone with it** (see above): the
viewer no longer shows the index, so the user can no longer be looking at a diff their edit is
absent from. What remains is the case that has nothing to do with the mode — the file is staged,
the index holds the old bytes, and the edit has to be restaged to be committed.

This is a well-known source of confusion in TortoiseGit. Handle it explicitly:

- Label the left side permanently in the viewer header: `Working tree ↔ HEAD` — or, in the log,
  the commit range the diff was computed over. The label is always what the left side actually is,
  which is the whole point of it, and the range case is why it is read from
  `SideBySideDiff.Range` first rather than from a field that could disagree with it.
- When the user edits a file that is already staged, show an inline strip: *"This file is
  staged. Your edit is not in the commit yet — restage?"* with a one-click restage
- On commit, restage every edited file if the user chose restage, and warn otherwise. Never
  silently commit a stale staged version of a file the user just edited.

## Guards

- Read-only for binary files, files above the size limit, and files in an unresolved conflict
- Never edit files outside the resolved repository root
- Refuse to save into a path that has become a symlink or junction since load

---

# Log

Commit history, and **the combined diff over a selection** — which is the whole reason this window
exists. Selecting several commits shows

```bash
git diff <oldest selected>^ <newest selected>
```

one command, always fast, that cannot fail. That answers *"what changed between Tuesday and now"*,
which reading five commits one at a time does not: their sum is not the same as five diffs. It is
the operation TortoiseGit is reached for and that almost nothing else offers, and a log window that
only showed one commit at a time would not have earned its place beside **Product Philosophy**.

```text
┌─ Log — d360-portal ──────────────────────────────────────────────────────────────────────┐
│ d360-portal                                    feature/storage-gw     400 commits loaded │
├──────────────────────────────────────────────────────────────────────────────────────────┤
│ 400 a1b2c3d  feat: add PgBouncer connection pooling HEAD ▸main Thomas Q. 2026-08-21 14:03│
│ 399 9f0e1d2  fix: pool leak on reconnect                       Thomas Q. 2026-08-21 09:40│
│ 398 77c4b10  chore: bump deps                                  renovate  2026-08-20 22:11│
│ 397 4d5e6f7  feat: storage gateway skeleton        v1.4.0      Thomas Q. 2026-08-19 16:02│
│ 400 commits loaded                                              [ Load 200 more ]        │
╞═════════════════════════════════════════════════════════════════════════════════════════╡
│ 3 commits · 4d5e6f7^..a1b2c3d   ·   including 1 you did not select                        │
│ feat: add PgBouncer connection pooling to the storage gateway                             │
│ Thomas Quemerais · 2026-08-21 14:03 · parent 9f0e1d2                                      │
├────────────────────────────────┬─────────────────────────────────────────────────────────┤
│ Changed files                  │ 4d5e6f7^ ↔ a1b2c3d                                       │
│  M src/GatewayClient.cs +42 -17│  41  services.AddSingleton   │ 41  services.AddSingleton │
│  A src/PgBouncerPool.cs +156   │  43- var pool = new Pool();  │ 43+ var pool = pooled(…)  │
│  D src/LegacyPool.cs      -203 │  ← read-only                              read-only →    │
├────────────────────────────────┴─────────────────────────────────────────────────────────┤
│ 12 files · +418 −233                  [ Create changelog… ]  [ Save as patch… ]  [ Close ]│
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## The number in front of the hash

The commit's own revision — how many commits it has behind it, itself included — because that is the
number `build.yml` stamps into a version as `0.0.<commits>`. Somebody holding a build and asking
*which commit is this* has a hash they cannot compute and a number they can read off the title bar,
and the log is where the two meet.

**One `rev-list --count HEAD` for the whole window, not one per row.** Git counts for a single
commit, and the list holds two hundred; the rows count down from the total by their position
instead. That is exact while the history is linear — every commit below a row is one of its
ancestors — and one too many for a row above a merge, where the list also holds commits that are
not. Counting each row exactly means walking the DAG and holding an ancestor set per commit, which
is not what a number in a gutter is worth. The count is read *beside* the first page rather than
before it, so the extra process costs no wall-clock against the 250 ms the window is measured on,
and a repository with no commits at all shows no number rather than a wrong one.

## The gap disclosure is a requirement, not a nicety

A **gapped** selection — commits 1, 2 and 5, skipping 3 and 4 — diffs `1^..5`, so the two skipped
commits are in the diff. That is the semantics that was chosen, and the alternative was rejected on
purpose: replaying only the picked patches onto a temporary tree can *refuse*, when a selected
commit does not apply without a skipped one, and a headline feature that sometimes says no is worth
less than one that is always right about a slightly wider range.

The price of that choice is a claim the user cannot verify by looking, so the window states it:

```text
3 commits · 4d5e6f7^..a1b2c3d   ·   including 1 you did not select
```

in the accent colour, in its own element, shown whenever the number is not zero. `ImplicitCount` is
computed in `CommitRange` where it is tested, never in the header's string formatting. **A combined
diff that quietly swept in commits the user did not pick is the one failure this window must not
have.**

## What it deliberately does not do

Written down once, because a list nobody wrote down grows one release at a time:

> **No checkout, reset, revert, cherry-pick, rebase, amend, tag-at-commit or branch-from-here.**

Nothing in this window writes to the repository. `HistoryService` reaches Git only through
`ReadAsync`, and a test asserts that every invocation the surface makes is a read — which is what
catches somebody later hanging a checkout off a right-click here.

There are two outward actions, and neither touches the repository. **Save as patch…** writes
`git diff --binary --output=<file>` at a path the user chose in a dialog, outside the repository.
`--output` rather than capturing stdout is load-bearing: the patch never becomes a C# string, so a
Latin-1 source file gets byte-exact bytes rather than U+FFFD, and the BOM question — a BOM in front
of `diff --git` makes `git apply` refuse the file — never arises. `--binary` is what makes the
result a patch that actually applies.

**Create changelog…** is the other, and it is the same range described for a different reader.

## The changelog

A patch is for a machine and a changelog is for a person — and the person is who the repository has
nothing for. `git log` answers *what did we do*, in the words of somebody who was mid-way through
doing it; nobody outside the team can read that, and turning it into something they can is the job
somebody does by hand before every release.

```text
┌─ Changelog — d360-portal ────────────────────────────────────────────┐
│ 3 commits · 4d5e6f7^..a1b2c3d   ·   including 1 you did not select   │
│ Style [ Brief                  ▾ ]                   [ Write again ] │
├──────────────────────────────────────────────────────────────────────┤
│ - Adds connection pooling, so the gateway stops running out of       │
│   connections under load                                             │
│ - Fixes the leak that made reconnects get slower over a long session │
├──────────────────────────────────────────────────────────────────────┤
│ Edit it here, then copy or save.  [ Copy ] [ Save as… ]    [ Close ] │
└──────────────────────────────────────────────────────────────────────┘
```

**It is written over the selected commits** — the same `CommitRange` the diff and the patch are of,
which is what `CommitRange.Commits` exists for: the commits the range *spans*, gaps included, sliced
in Core where the newest-first arithmetic is tested. So the gap disclosure is repeated in this
window's header rather than left behind in the one that produced the selection. A changelog quietly
describing a narrower range than the diff beside it would be the same failure that disclosure exists
to prevent.

**The payload is the commit subjects and the range's diff**, through the same `AiContextBuilder`
every other surface uses — so a lock file, a minified bundle and a secret-matching path are held
back here by the same code and for the same reason. It carries no branch name and no hashes, which
is the one place its payload differs from the other two: those are precisely what the prompt asks
the model to keep out of a changelog, and the cheapest way to keep them out of the answer is to keep
them out of the question.

**Brief or Detailed, and the choice is a line of the payload rather than part of the prompt.** That
is the whole shape of `ChangelogPrompt`. The system prompt is a file the user owns — see
**Prompt** — and a file is sent verbatim, so a length rule written into the built-in prompt would
silently stop working the moment anybody edited theirs, leaving a box in the window that does
nothing. As the payload's last line it reads as what it is: an instruction about this request, not a
rule about changelogs. A user's own prompt keeps working, and the box keeps meaning something. It is
chosen per changelog and persisted nowhere; a `settings.json` key for it would be Hard
Requirement 2's setting nobody asked for.

**With no AI configured the window still works**, which is what "the AI is an accelerator, never a
dependency" requires of a window whose only content the AI writes: the box opens holding the commit
subjects as a bulleted list, oldest first, and that is a serviceable changelog rather than a
placeholder. It is also what is on screen while the first tokens arrive.

**The text is a draft in a box** — editable, copyable, savable, and gone when the window closes
unless the user does one of those three things. There is no working tree on the other side of it, so
an edit costs a keystroke and risks nothing, and that is what makes the Style box safe to press
twice. Typing wins over a stream still arriving, the pull-request window's rule; **Write again** and
a style change both override that, because both *are* the user asking. Saving writes UTF-8 without a
BOM into the repository's parent — the patch's directory rule, for the patch's reason: a file
dropped inside the working tree comes straight back as an untracked row in the commit window.

**No version number, no date, no `CHANGELOG.md`.** Deciding which version this is, and appending to
a file that is then committed, are both writes to the repository — which is the line this window
does not cross. It produces the text and hands it over; where it goes is the user's.

## Reading history

```bash
git log --decorate=short --no-color --max-count=<page+1> --format=<machine format>
git diff --name-status -z -M --no-color --no-ext-diff --no-textconv <base> <tip>
git diff --numstat     -z -M --no-color --no-ext-diff --no-textconv <base> <tip>
```

- **The format is `%H%x1f%h%x1f%P%x1f%an%x1f%aI%x1f%D%x1f%B%x00`.** `%B` is last and is the only
  free-text field, and the split is bounded at the field count — so a message containing a newline,
  a separator byte or anything else lands in the final slot verbatim and cannot shift a field.
  Records end with NUL, the one byte a commit message cannot contain.
- **`tformat` appends a newline after every record**, *after* our NUL. Every record but the first
  therefore arrives with a leading newline, and the stream ends with a record that is nothing else.
  Not trimming it makes every sha after the first begin with `
`, and nothing matches.
- **`%P` is empty for the root commit.** Split without `RemoveEmptyEntries` it reports one parent
  whose sha is the empty string, which becomes a base spec of `""` — turning the repository's first
  commit into a Git error rather than a diff against the empty tree.
- **`--name-status -z` is a third parser**, beside porcelain v2 and numstat, because there is no
  other way to get the letter for a range: `--porcelain=v2` is working-tree-only by construction,
  and inferring the letter from the counts is wrong in both directions. Its trap is its own: the
  similarity score is glued to the letter (`R100`), and a rename consumes **two** extra fields where
  every other record consumes one.
- **Paging is `--skip`**, never a "start from the last sha I saw" cursor, which is wrong twice:
  `<sha>^` does not resolve when the last row of a page is the root commit, and when it is a merge
  the caret silently switches the walk to the first-parent line, so the next page is a different set
  of commits from the one being scrolled through.

## The range

Resolved by `CommitRange.Resolve`, a pure function in Core, for the reason `CommitFlow` is there:
the list is **newest-first**, so the newest selected commit is the *lowest* index and the oldest is
the highest — the one place in the feature where the arithmetic reads backwards, and "the range came
out the wrong way round" is exactly the bug clicking does not reveal, because both ends are plausible
hashes either way.

| Selection | Base | Tip |
|---|---|---|
| one ordinary commit | its `Parents[0]` | itself |
| N commits, contiguous or gapped | oldest's `Parents[0]` | newest |
| oldest is the **root** | the empty tree, `4b825dc…` | newest |
| oldest is a **merge** | `Parents[0]` — first parent, no special case | newest |

The base is always a **bare object id**, never revision syntax, so nothing downstream has to know
Git's revision grammar. The empty tree needing no code path of its own is the payoff: a root commit
fails the blob lookup exactly the way an added file does, and shows as a whole file inserted.

A merge selected alone gives `git diff <merge>^1 <merge>` — "what this merge brought in", which is
the useful answer and the one `git show <merge>` conspicuously does not give. The second parent
would invert it, rendering every change from the other branch as a deletion.

## The viewer is the commit window's, read-only

`SideBySideDiff.Range` is non-null for a historical diff, and `IsEditable` consults it before
anything else. That one property is the whole integration: `DiffPane` already calls
`SetEditable(diff.IsEditable, …)`, which makes the right editor read-only, blanks the caret, **hides
the entire editing bar** rather than disabling its buttons, and swaps the footer label — and both
`OnRightTextChanged` and the re-diff timer already return early when the editor is read-only.

Carrying the range rather than a bare flag also gives the header its text (`a1b2c3d^ ↔ e4f5g6h`)
with no second field to keep in step, so a historical diff *cannot* be rendered under a label
reading "Working tree ↔ HEAD". Given that the whole **staged-versus-worktree trap** section exists
because a mislabelled header is how users lose work, that is the mistake worth making impossible.

## Scope of the listing

The current branch, `git log HEAD`, and no branch picker. `flick log <path> <rev>` is not built
either — the ComboBox-shaped feature this would grow into is the full client the tool is not. What
exists is a list, a selection and a diff.

---

# Blame

Who last touched each line of a file — and, the reason this exists, **what was there before**.

Reading one blame answers a question about the present. The commit it names is very often not the
one that introduced the line, only the last to reformat, rename or move it. Stepping back is how you
get past that to the change that actually did it, and it is the half most blame viewers leave out.

```text
┌─ Blame — VerbRunner.cs ──────────────────────────────────────────────────────┐
│ src/FlickGit.App/CommandLine/VerbRunner.cs   at 6b04582 · Initial commit      │
│                                                            1 back   [ ← Back ]│
├──────────────────────────────────────────────────────────────────────────────┤
│ 6b04582 o0Zz     2026-08-22 │  1 │ using FlickGit.Actions;                    │
│                             │  2 │ using FlickGit.Cli;                        │
│ 498bb03 o0Zz     2026-08-23 │  3 │ using FlickGit.App.Localization;           │
│                             │  4 │                                            │
├──────────────────────────────────────────────────────────────────────────────┤
│ 498bb03  Added multilanguage                                                 │
│ o0Zz · 2026-08-23 10:14 · line 3                                             │
│ 222 lines · 7 commits    [ Blame previous revision (6b04582) ]    [ Close ]   │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Git computes the step, not the window

```bash
git blame --porcelain [<revision>] -- <path>
```

The porcelain stream emits, per commit, `previous <sha> <path>` — **the commit to blame next and the
name the file had there**. So "blame the previous version" is the same command with those two
values, and three things follow for free:

- **Nothing appends `^` or resolves a parent.** The same rule `CommitRange.BaseSpec` already set: Git
  hands over a bare object id, and no code here has to know Git's revision grammar.
- **A rename is followed** by using the path Git reported rather than the one the walk arrived with.
  The header changes to the old name, which is how the user learns a rename happened.
- **`boundary` ends the walk** honestly. The button then says *"This is the first commit that touched
  the file"* rather than being merely greyed out.

Clicking a line selects it: the band names its commit, every other line that commit is responsible
for lights up, and the button names where a step would land — `Blame previous revision (6b04582)` —
so pressing it is never a guess. `Alt+←` and Back return, **restoring the caret line as well as the
revision**, or stepping back and forward would lose the line the walk was following, which is the
whole thing being followed.

## The porcelain traps

`--porcelain` is line-oriented, so it is the one parser in the product that does not go through
`NulFieldReader` — and it is still machine-readable output rather than the human form CLAUDE.md
forbids parsing: plain `git blame` is a column layout that moves with the terminal width and the
user's `blame.*` settings.

- **Metadata appears once per commit, not once per line.** Every later line of the same commit
  carries the bare header, so commits are cached by sha and re-attached. A parser that expects the
  block every time keeps the author on the first line of each run and blanks the rest.
- **The content line is found by its leading TAB**, never by exhausting the known keys. A `summary`
  is arbitrary user text and a `filename` is an arbitrary path; a commit message shaped like a header
  field would otherwise shift the parse.
- **A sha of forty zeros is "not committed yet"** — the ordinary result of blaming the working tree,
  which is what a right-click on a file does. Git still emits a `previous` for it, so the walk works
  from an unsaved edit and lands on the committed version, which is exactly "what was here before I
  touched it".
- **`author-time` is epoch seconds with a separate `author-tz`.** The zone is kept rather than
  converted, so a commit made elsewhere reads as the hour its author saw.

**No `--no-color`**, unlike every other command in the product: the porcelain format carries no
colour whatever `color.ui` says, and Hard Requirement 2 rules out a flag that does nothing.
**`blame.ignoreRevsFile` is deliberately honoured** — a user who configured a `.git-blame-ignore-revs`
did it so a bulk reformat stops masking authorship, and overriding it would be overriding the answer
they asked for. `-M` and `-C` are not passed: real options with a real cost that nobody asked for.

**A binary file is refused by us, not by Git.** `git blame` does not fail on one, it blames it into
nonsense — one "line" per run of bytes that happened to contain a newline. The parsed text is sniffed
for NUL and the window says so instead of showing mojibake.

## The gutter is a margin, not a column

One read-only AvalonEdit editor with a `BlameMargin`, so the annotation stays put while a long line
scrolls sideways and the code keeps its syntax highlighting. Per CLAUDE.md's "never insert a visual
element per line", a whole screen costs one `DrawingContext`; a list of rows would cost a `Grid` and
four `TextBlock`s each.

**The annotation is drawn once per run of the same commit**, not once per line. Twenty consecutive
lines repeating one hash is how a blame becomes unreadable — the eye is looking for where authorship
*changes*, and only the first line of a run carries that.

## What it deliberately does not do

> **No checkout, reset, revert, cherry-pick, rebase, amend or tag-at-commit.**

The same boundary the log window holds, and for the same reason: reading history changes nothing, so
it belongs in a tool that is not a complete Git client. `BlameService` reaches Git only through
`ReadAsync`, and a test asserts every invocation is a read.

Scope of the listing: the current branch's history of that one file. No `-L` line range, no author
filter, no "blame this at a branch" picker.

---

# Commit

```bash
git commit -F "<temp-file>"
```

Always use a temp file, even for single-line messages — it avoids all quoting and
interpolation questions. Delete it afterwards, including on failure.

Do not commit if no files are staged or the message is empty. Show a clear reason.

After success:

- Display the short hash from `git rev-parse --short HEAD`
- Optionally close automatically (setting)
- Optionally offer Push (setting)

---

# Commit & Push

Sequence, stopping at the first failure, reporting the failing step:

```bash
git -C <repo> add -- <resolved paths>
git -C <repo> commit -F <temp-file>
git -C <repo> push                      # or push -u origin HEAD
```

Guardrails, checked **before** executing:

- **No upstream:** ask once, remember the answer per repository
- **Behind the remote:** offer `pull --rebase --autostash` then push as a single button. Do
  not push and let it fail.
- **Diverged, or push would require force:** stop. Never offer force-push from any surface, and
  never as part of a commit.
- **On the primary branch:** show a warning strip if `Warn when committing to main` is
  enabled (default: on). This is the one case where the fast path deserves friction.
- Secret detection runs before the commit, not only before the AI call.

Budget for the Git portion, warm, excluding network: **400 ms**.

---

# Branch Selector

There is **no separate "Commit in new branch" action.** Branch choice is an editable
ComboBox inside the commit window. This removes a menu entry and a decision the user would
otherwise have to make before seeing their changes.

```text
Branch: [ feature/storage-gw            ▾ ]
          ├ feature/storage-gw   (current)
          ├ main
          ├ develop
          ├ fix/pool-leak
          └ …type a new name to create it
```

- Default value: the current branch. Committing without touching the ComboBox is the normal
  case and must involve no extra Git work.
- Dropdown: local branches from `git for-each-ref --format=%(refname:short) refs/heads`,
  current branch first, then MRU order.
- Free text: if the typed value is not an existing branch, it is treated as a new branch
  name.

## Resolution on commit

```text
typed value == current branch   → commit, push
typed value is an existing ref  → switch, refresh, commit, push
typed value is new              → validate, create, commit, push -u
```

**Existing branch:**

```bash
git switch "<branch>"
```

`git switch` carries uncommitted changes across when there is no conflict, and refuses when
there is. If it fails, **stop** — do not stash, do not force. Report which files block the
switch and leave everything untouched.

After a successful switch, the diff the user reviewed was computed against the **old**
branch's HEAD. Refresh the file list and recompute the counts before committing. If any
selected file's content or status changed as a result of the switch, abort and show the
refreshed list rather than committing something the user has not seen.

**New branch:**

```bash
git check-ref-format --branch "<branch>"
git switch -c "<branch>"
git commit -F <temp-file>
git push -u origin HEAD
```

Validate before creating. If creation fails, do not commit. The branch is created from the
currently checked-out commit unless the user explicitly chooses otherwise.

**Order matters:** switch or create the branch *before* committing, so the commit lands on
the intended branch. Staging is index-based and survives the switch, so stage first, switch
second, commit third.

The ComboBox shows the resolution inline as the user types, so the consequence is visible
before Enter:

```text
Branch: [ fix/pool-leak                 ▾ ]  ← existing, will switch
Branch: [ feature/new-thing             ▾ ]  ← new, will be created
Branch: [ feature/bad..name             ▾ ]  ← invalid ref name
```

Committing to the primary branch through the ComboBox triggers the same warning strip as
anywhere else.

---

# Primary Branch Resolution

There is **no separate "Commit to main" action** — the user types `main` in the branch
ComboBox. But the tool still needs to know which branch is primary, in order to show the
warning strip.

Resolution order:

1. **`flickgit.primaryBranch`, in the repository's own config**
2. User setting
3. Remote HEAD — `git symbolic-ref refs/remotes/origin/HEAD`
4. `main`
5. `master`

The repository's own answer goes first, because the more specific setting wins: a user with `main`
configured globally and one repository still on `develop` would otherwise be warned about the wrong
branch on every commit — the friction this exists to add, aimed at the wrong target. See
**Repository Settings**.

Cache the result per repository — but **only the answer that costs a ref lookup**. Neither
configured value is cached: the override is one `config --get`, so it is always current and the
window that writes it needs no way to invalidate anything. Resolving this must never block the menu or the popup: if
resolution has not completed, show the popup without the warning strip rather than waiting.

Switching to the primary branch follows the ordinary switch rules — check
`git status --porcelain`, never discard anything, stop and explain if the switch is refused.

---

# Pull --rebase

```bash
git pull --rebase --autostash
git submodule update --init --recursive    # only when .gitmodules exists
```

Show progress in a lightweight dialog, with the submodule update as a distinct step (see
**Submodules**). On conflict, show a clear message and offer to open
the repository status window. Do not automatically abort a rebase.

**`--autostash` is unconditional, and there is no second verb without it.** This section used
to specify both spellings and let the menu carry the plain one, which meant the everyday entry
refused to run whenever the working tree was dirty — which is most of the time, since the user
reaching for Pull is usually part-way through something. "Commit first, then pull" is not an
answer a one-click menu entry gets to give.

Git stashes only when there is something to stash, restores it when the rebase finishes, and
unwinds the whole thing itself if the rebase fails. That is why the flag is Git's rather than a
stash/pull/pop sequence of ours: there is no window in which a stash exists that nothing is
tracking. A rebase that stops on conflicts still stops — the autostash is restored by
`git rebase --continue` or `--abort`, and the user is told which.

**The window closes itself on a clean pull when `Close the pull window after a successful pull` is
on**, off by default. A step list with every step ticked says only "yes", and paying a keystroke for
that on an action performed several times a day is the trade the commit window's own
close-after-success setting already makes. **Only a clean pull.** A failure keeps the window, with
the Git error and the next command on it, and so does a pull whose submodule update failed — both
have something to report, and a window that closes cannot report it. So silence means success, which
is the only thing this setting is allowed to make it mean.

The switch is in the settings window rather than on the progress window itself, which is where a
checkbox for it would be more discoverable and where it could not be unticked: the first pull after
ticking it takes the window away before the box can be reached again.

---

# Push

```bash
git push
git push -u origin HEAD    # when the branch has no upstream
```

Ask before creating an upstream. Remember the answer per repository.

---

# Pull Requests

Propose the current branch, on **GitHub, GitLab or Azure DevOps**, cloud or self-hosted. Reached
from the FlickGit submenu, the palette and `flick pr`.

```text
┌─ Pull request — d360-portal ─────────────────────────────────────────────────┐
│ feature/storage-gw  →  [ main            ▾ ]           GitHub · o0Zz/portal   │
│ 3 commits · 12 files · +418 −233                                              │
├──────────────────────────────────────────────────────────────────────────────┤
│ Title                                                                         │
│ feat: add PgBouncer connection pooling to the storage gateway                 │
│ Description                                                                   │
│ Adds a pooled connection path in front of the storage gateway…                │
│                                                                               │
│ ☐ Draft   ☑ Delete feature/storage-gw when it merges     [ Write with AI ]    │
├──────────────────────────────────────────────────────────────────────────────┤
│ ⏎ create   esc close              [ Create pull request ]        [ Close ]    │
└──────────────────────────────────────────────────────────────────────────────┘
```

It is the commit window's shape on purpose, minus the file list. A pull request is reviewed on the
server, so the question here is *"is this the right branch going to the right place"*, which one
summary line answers — not *"which of these files"*, which is what the file list is for.

## The order is the feature

`PullRequestFlow` in Core, with tests, for the reason `CommitFlow` is there:

```text
push the branch → find an existing request → create → open it in the browser
```

**The push is first and it is not optional.** Everything after it asks a server about a branch, and
until it has run the server does not have one — a request created first is a 404 about a branch the
user is looking at. The push goes through `PushService`, which is what keeps this surface from being
a way around the push guardrails: **a diverged branch is refused here exactly as it is from the
commit window, and force-push is not reachable from either.** A branch behind its own upstream is
refused too — somebody else has pushed to it, and proposing without their commits would open a
request missing work already published under that name.

**Creating an upstream is consent**, asked through the same `UpstreamConsent` the commit surface
uses, so "once per repository" means once across both. Declining stops the flow; it does not fall
through to creating the request, which is a bug the flow's tests caught and now pin.

**The existing-request check is not an optimisation.** All three services refuse a duplicate with a
status code and none of them says *where* the existing one is. `!12 is already open for this branch`
is an answer; `409 Conflict` is a puzzle. It runs before the create, and again on window open when a
credential is already on the machine — never prompting for one, because demanding a token for a
check the user did not ask for is the wrong first thing this feature can do.

## Credentials: Git's, then ours

```text
a token FlickGit stored for this host  →  git credential fill  →  ask once, and store it
```

**The middle step is the whole reason this needs no setup.** A developer who can `git push` to
github.com has Git Credential Manager holding a token for it, and that token is what the REST API
wants. `git credential fill` runs with `credential.interactive=false`, so a helper with nothing
stored answers with nothing rather than opening somebody's browser tab in the middle of a click.

A stored token comes *first*, not last: the only reason one exists is that the user stored it,
because what the helper had did not work. Trying the helper again ahead of it would re-break the
thing they fixed.

Tokens are filed in Windows Credential Manager as `FlickGit:forge:<host>` — per host, because that
is what a credential is actually scoped to. One token opens requests on every repository on
`github.com`; a company with both `dev.azure.com` and an internal GitLab needs two. `ApiKeyStore`
became `CredentialStore` for this: what varies between an API key and a forge token is the name it
is filed under, so that became the parameter rather than a second copy of four P/Invokes.

**A 401 is retried once, and only a 401.** A helper's token can be stale in a way nothing local can
detect, and the remedy — ask the user — is what the flow would do next time anyway. Any other
failure is reported as it stands: retrying a request the server has already explained is guessing.

## Three APIs, three shapes

| | GitHub | GitLab | Azure DevOps |
|---|---|---|---|
| API base | `api.github.com`, or `/api/v3/` on Enterprise | `https://host/api/v4/` | the **collection** URL |
| auth | `Bearer` | `Bearer` | **Basic**, token as the password |
| draft | a `draft` field | a `Draft:` title prefix | `isDraft` |
| delete on merge | **no per-request setting** | `remove_source_branch` | `completionOptions` |
| the number | `number` | **`iid`**, not `id` | `pullRequestId` |
| web URL | in the answer | in the answer | **built**, not returned |

`IPullRequestClient` has three implementations, which is what earns it under Hard Requirement 2 —
and they share almost nothing but `ForgeApi`, which carries the timeout, the user agent, the
redaction and the status-code wording. Three of the rows above are traps rather than differences:
GitLab's `id` is globally unique and appears nowhere in its interface, Azure DevOps answers a Bearer
token with an HTML sign-in page, and a GitHub delete-on-merge checkbox would silently do nothing —
so it is hidden there rather than sent and ignored.

Responses are read with `JsonDocument` rather than typed DTOs, deliberately: the three disagree
about the shape of an *error* — GitLab's `message` is a string, an array of strings or an object of
arrays depending on what failed — and a DTO per variant would be a dozen types expressing "find me a
sentence to show the user". Requests are source-generated, because Core is AOT-compatible.

The Azure DevOps `api-version` is pinned to **6.0** rather than the current 7.1. Everything sent has
existed since 5.1, so the newer version buys nothing — and Azure DevOps *Server* installs a few years
old answer 400 to a version they predate. The lowest version carrying the fields is the one that
works in the most places, which is what a per-user tool running against whatever a company happens to
host needs.

## Which repository, and which branch

`ForgeUrl` is the parser, and **the one place in this feature where a wrong answer is expensive**:
every other mistake is an error message, and this one would open a pull request against a real
repository that is not the user's. It handles `https`, `ssh`, `git` and scp-style remotes, GitHub
Enterprise, nested GitLab subgroups, `dev.azure.com`, `*.visualstudio.com`, the `v3/` SSH form and
Azure DevOps Server behind any collection path. Azure's API hangs off the collection, which is
"everything in front of the project" rather than a known prefix — that is what makes Services and
Server one code path.

It is deliberately **not** `CloneUrl`. That one guards a clipboard prefill and is allowed to refuse a
valid URL, because a wrong prefill costs the user more than an empty one. This one answers "which
project, on which API", where refusing is the safe failure and guessing is not.

**An unrecognised host is refused rather than guessed at.** `git.acme.io` is a GitLab or a GitHub
Enterprise with equal probability, and posting a request shaped for the wrong API at whatever is
listening is the one mistake with no way back. The refusal names the fix:

```bash
git config --local flickgit.forge gitlab
```

**The target is the primary branch the rest of the product already resolves** —
`flickgit.primaryBranch`, the user setting, `origin/HEAD`, `main`, `master` — with
`flickgit.pullRequestTarget` in front of it for a repository that proposes into `develop`. Two keys
rather than one, because they answer different questions and a GitFlow repository gives them
different answers: the primary branch is what the commit window *warns about committing to*, and one
key would force the user to choose which of the two features is allowed to be right. The box is an
editable ComboBox over the branches that exist on the remote — not the local ones, because a target
the server does not have is a request no service will accept.

**Which remote, and which of its two URLs.** The branch's own tracked remote first — a branch pushed
to a fork should propose from the fork — then `origin`, then whatever single remote exists. And its
**push URL** when it has a separate one, because `git push` obeys `remote.<name>.pushurl` and the
request has to be opened on whatever project the branch actually landed in. Reading the fetch URL
instead breaks the one workflow where the two differ — fetch from upstream, push to a fork — by
pushing the branch to the fork and then asking upstream to review a branch it has never heard of.

**Nothing here touches the network before the window paints.** The remote list is a config read, the
branch list is `for-each-ref` over refs already fetched, and the merge base is a walk of the object
database — the same rule `PushService.PlanAsync` follows.

**No fork support.** The source and the target are branches of one repository. A cross-fork request
needs a head qualified with another owner, a second remote to resolve it from and a permissions model
per service; nothing here pretends otherwise.

## The description

The same providers, a different prompt. `IAiGenerator` takes an `AiPrompt` — a system prompt, a
payload and a token ceiling — where it used to take a `CommitContext`, which is what lets a second
surface use the streaming, the timeout, the redaction and the failure counter rather than owning a
copy. Per Hard Requirement 1 the signature changed rather than gaining an overload beside it, and
`CommitMessageService` became `AiTextService` for the same reason. The consecutive-failure count is
**not** duplicated: three failures raise one tray warning whether they came from commit messages,
descriptions or a mix, because what the user needs to know is that the provider is not working, not
which button noticed.

**One request, not two.** The answer's first line is the title, then a blank line, then Markdown —
the shape a commit message already has, so the parsing rule is Git's own. Two requests would double
the latency to have a model read the same diff twice, and risk a title describing something the body
does not. It is split on every fragment, so the title box fills in first and the description grows
underneath it.

**The commit subjects come before the diff in the payload**, which is the whole reason there is a
second context builder. A commit message is written from a diff because there is nothing else; a
branch has already been described, one commit at a time, by the person who wrote it. Those lines are
the best statement of intent available and the cheapest — so a model reading a truncated diff still
has them.

Everything about *what may leave the machine* is `DiffPayload`, unchanged and not reimplemented: a
lock file, a minified bundle or a secret-matching path is held back here by the same code and for the
same reason as in a commit message. The diff is read against the **merge base**, which is what a
forge shows — against the target's tip it would put every commit made on the target since the branch
started into the payload, and the model would faithfully describe somebody else's work.

`AiOptions.MaxOutputTokens` became two constants: 150 still guards a commit subject, and 700 guards a
description, which is a title plus a few paragraphs of Markdown. One number would either truncate the
second or stop guarding the first.

**The prompt is a file too**, `%LOCALAPPDATA%\FlickGit\pull-request-prompt.md`, on the same terms as the
commit one and the changelog one — see **Prompt**. Its seeded header carries one rule the other does not: keep the
first-line-is-the-title shape, because `PullRequestPrompt.Split` is what fills the two boxes, and a
prompt that asks for JSON or a `Title:` label puts that text in the title box.

**With no AI configured the window still works**, which is what "the AI is an accelerator, never a
dependency" requires: the title prefills from the single commit's subject or from the branch name,
and the description from the commit subjects as a bulleted list. Neither is ever overwritten once the
user has typed — an explicit press of **Write with AI** is the only thing that overrides that, because
it *is* the user asking.

## What it deliberately does not do

> **No reviewers, no labels, no work items, no merging, no approving, no comments.**

Reviewers were the one real candidate and were left out: each service needs a user-search call with a
different shape and a different id type — a login, a numeric user id, an Azure DevOps descriptor —
and a typed field with no completion behind it is worse than no field. Merging and approving are the
other half of a code-review tool, which is the full client this is not: FlickGit opens the request and
hands it to the browser, which is where it is read anyway.

One outward action beyond the create: **the finished request is opened in the browser**, and its URL
is checked for an `http`/`https` scheme first. That string arrives over the network, and
`UseShellExecute` starts whatever a scheme is registered to.

---

# Resident Service

The single biggest lever on perceived speed. A cold WPF start pays CLR startup, JIT,
`PresentationFramework`/`PresentationCore`/`WindowsBase` load, theme dictionary resolution,
HWND creation and first render — typically 400–800 ms. Pay it once at login instead of on
every right-click.

## Warm-up

At startup, after the tray icon is created:

1. Construct the commit window and the palette, but never call `Show()`
2. Force template application and a measure/arrange pass so WPF resolves themes and JITs the
   layout path
3. Keep the instances alive; on request, reset the `DataContext` and `Show()`

Also warm: the AvalonEdit control, the HTTP connection to the AI provider, and the
repository-root cache for the MRU list.

## IPC

Named pipe, per user, per session:

```text
\\.\pipe\flickgit.{userSid}.{sessionId}
```

- `PipeSecurity` grants access to the **current user SID only**. This pipe can trigger
  process execution through user-defined actions; a world-readable pipe would be a local
  privilege escalation vector.
- Length-prefixed UTF-8 JSON framing. One request, one response.
- Client timeout 250 ms.
- On timeout or missing pipe, the CLI **falls back to launching `FlickGit.App.exe` directly
  with the same arguments**. The resident service is an optimisation, never a dependency.
  Every feature must work with it disabled.

## Foreground activation

A background process cannot steal focus. Without this, the window opens behind Explorer.

- The CLI process was launched from user input and holds foreground rights. It calls
  `AllowSetForegroundWindow(residentPid)` **before** sending the request, and exits only
  after the response arrives.
- The resident service then calls `SetForegroundWindow` successfully.
- For the hotkey path this is unnecessary: receiving `WM_HOTKEY` grants foreground
  activation rights directly.
- **The hook path grants nothing.** A low-level hook proc runs before the input reaches any thread's
  queue, and swallowing the key means no queue ever gets it — so nothing credits this process with
  the last input event and `SetForegroundWindow` may be refused. The popup therefore *checks*
  `GetForegroundWindow` after activating and, if it was refused, drops `Topmost` and stops closing
  on focus loss. A `Topmost` popup without keyboard focus over an Explorer window is worse than no
  popup at all: Enter reaches Explorer's file list and opens whatever was selected.

## Lifecycle

- Single instance via named mutex; a second launch forwards to the first
- Autostart via a Scheduled Task at logon with a 30–60 s delay, so the tool never appears in
  boot-impact measurements. `HKCU\...\CurrentVersion\Run` is an acceptable fallback.
- Tray menu: Recent repositories, Settings, About, Exit. Left-click opens the menu rather than
  committing: there is no Explorer window behind a tray click, so there is no folder to resolve and
  no repository worth guessing.
- **No "Quick commit" entry.** It opened the popup, which is gone. Nothing replaced it, for the
  reason above: the recent list is the honest way to name a repository from here.
- **No "Pause shell integration" entry.** It wrote the same registry keys the Settings window's
  context-menu checkbox writes, permanently, under a word that promises it comes back on its own --
  a mislabelled duplicate of one boolean, not a second feature. The checkbox is the only surface
  for it.
- Idle working set target **80 MB**. Do not call `SetProcessWorkingSetSize` to fake a lower
  number — the pages return on first use and the window becomes slow again, which is the
  exact opposite of the goal.
- No `FileSystemWatcher` on working trees. Status is computed on demand and cached briefly.

---

# The Explorer Trigger

The primary interaction. The user is already in the right folder, so the context is free.

```text
User is browsing C:\dev\d360-portal in Explorer
        ↓  presses the trigger
        ↓  < 120 ms
Commit window appears, caret in the message box
        ↓  AI message streams in
        ↓  Enter
Commit + push, toast confirmation, window closes
```

**This opened a dedicated popup until it did not.** The popup was smaller than the commit window on
purpose — no file list, no diff — and the commit window was the escape hatch behind it. Both are now
the same window: see **Commit Window**, which carries the argument for the merge and the keyboard map
that came out of it. What this section still owns is everything before the window appears — which key,
and which folder.

## Trigger: three mechanisms, global hotkey by default

**The default is `Ctrl+Alt+G` through `RegisterHotKey`, and it installs no hook at all.** A global
low-level input hook on a first run by an unsigned binary is what EDR products flag, and a feature
the antivirus quarantines is worth less than one that claims a single key combination. The
Explorer-scoped mechanisms below are built and selectable, and off until the user turns one on —
which is what "make the hook an opt-in setting" at the end of this section actually requires.

The rest of this section is the argument for the *optional* hook, and it stands:

`RegisterHotKey` claims a key **globally**, ahead of every application. Binding F12 that way
would remove DevTools from every browser and the debugger shortcut from Visual Studio.

Use a **low-level hook** and swallow the key **only when the foreground window belongs to
`explorer.exe`**. Everywhere else it passes through untouched. This works for both
`WH_KEYBOARD_LL` (F12) and `WH_MOUSE_LL` (a mouse side button, `XBUTTON1`/`XBUTTON2` — often
the fastest gesture of all, since the hand never leaves the mouse).

```text
hook proc:
    if (input != configuredTrigger)          return CallNextHookEx(...)
    if (cachedForegroundPid != explorerPid)  return CallNextHookEx(...)
    PostMessage(residentWindow, WM_FLICK_TRIGGER, 0, 0)
    return 1                                 // swallow
```

Hard constraints:

- The hook proc runs on **every input event system-wide**. If it exceeds
  `LowLevelHooksTimeout` (default 300 ms), Windows silently unhooks it and the feature dies
  with no error.
- Therefore: **never** call `GetForegroundWindow` or `GetWindowThreadProcessId` inside the
  hook. Maintain the cached state from a separate `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`
  callback and compare only that.
- **Cache the verdict, not the PID.** Explorer's process id changes on every Explorer restart, is
  plural when *Launch folder windows in a separate process* is on, and is a third value for the
  instance that owns the desktop. Cache `explorerIsForeground` as a bool, resolved in the win-event
  callback (which *is* allowed to call `GetWindowThreadProcessId`), and an Explorer restart becomes
  a non-event rather than something that has to be detected.
- Never do Git work in the hook. `PostMessage` and return.
- The hook lives in the resident service. Degrade gracefully when it is not running.
- A global input hook may be flagged by EDR/antivirus. Sign the binary, make the hook an
  opt-in setting, and offer `RegisterHotKey` on a non-conflicting combination as the
  alternative.

Settings expose all three, once all three exist:

```text
Trigger:  (•) Global hotkey          [ Ctrl+Alt+G ]
          ( ) Explorer-only key      [ F12        ]   (uses an input hook)
          ( ) Explorer-only mouse    [ Side button 1 ]
```

Until the hooks are built there is **no settings value for them**. A setting that silently falls
back to something else is worse than one that does not exist, and Hard Requirement 2 rules out a
setting nobody can use.

## Folder resolution

On `WM_FLICK_TRIGGER`, resolve the folder shown by the foreground Explorer window via the
`IShellWindows` shell automation interface, then normalise to the repository root.

Order:

1. Selected item in the active Explorer view, if it is a folder
2. Current folder of the active Explorer window

**There is no third step.** There was: the most-recently-used repository, as a fallback the popup
labelled in its header. It is gone, and **a trigger with no Explorer folder behind it now opens
nothing at all** — no window, no notification, one debug log line. That covers the hotkey pressed
from an IDE and the tray icon clicked with no Explorer running.

The rule this serves is the one below: never act on a repository the user is not looking at. A
labelled guess satisfied it in a popup that was three lines tall and dismissed itself on focus loss.
The commit window is neither, and "opens the wrong repository, but says so in the header" is a worse
failure in a window the user has to read and close. Not guessing is cheaper than labelling the guess.

**Windows 11 tabbed Explorer:** one HWND hosts several tabs, so matching by HWND can return
an inactive tab's path. Compare the resolved path against the window's address-bar title
where possible. Two tabs on folders with the same leaf name are genuinely undecidable; the first
candidate is used and the ambiguity is logged, because the window names the repository in its own
title bar and header — which is what "show the repository name prominently" asks for. `Ctrl+R` to
switch repository was the popup's answer to this and went with it: the window has a title bar, a
file list and a Cancel button, so a user looking at the wrong repository can see it and close it.

If the folder is not inside a repository, show the clone dialog instead of the commit
window (see **Clone**). `git init` is offered as a secondary choice, not the default.

## What opens

The commit window, pre-warmed, through the same host the context menu and `flick commit` use. Not
placed near the cursor: it is a full window with a title bar, so it keeps the position WPF gives it
rather than being anchored like the popup was, and it does **not** close on focus loss — an
accidental click outside must not throw away a message the user is typing.

Three behaviours the popup could not have, because it had no file list, come for free and the
CLAUDE.md text that worked around them is deleted rather than reinterpreted:

- `CommitFlowOutcome.AbortedSelectionChanged` says "the list has been refreshed" — and now there is
  a list, showing exactly what changed under the user.
- "If the resulting set is empty, say so and disable Commit" needs no way out: the tick boxes are
  right there.
- Nothing has to name `Details…`, because there is nothing to hand off to.

## Queued Enter

If the user presses Enter before the AI message has arrived, **do not block and do not
refuse**. Queue the commit: the button switches to a progress state and the commit fires the
instant the message lands.

This is what makes the true one-key path work — trigger, Enter, done, without waiting to read
anything. Cancellable with Esc until the commit actually executes.

If generation **fails** while a commit is queued: cancel the queue, focus the message box,
keep the window open. **Never commit an empty or placeholder message.**

---

# AI Commit Messages

## Model selection

The task is short-output summarisation: large-ish input (a diff), tiny output (one line plus
an optional body). It does not benefit from reasoning. Pick the fastest tier and turn
reasoning off.

**Anthropic (default):**

```text
Model:             claude-haiku-4-5-20251001
Extended thinking: not enabled
max_tokens:        150
stream:            true
```

Haiku 4.5 is the fastest and cheapest current Claude model, at $1 / $5 per million
input / output tokens. Extended thinking exists on the Haiku line — do not enable it here.

**OpenAI:**

```text
Model:             gpt-5.6-luna
reasoning:         { "effort": "none" }
max_output_tokens: 150
stream:            true
```

GPT-5.6 Luna is the cost-optimised tier at $0.20 / $1.20 per million tokens. Reasoning
effort is a per-request parameter; `none` is the latency baseline, `low` the next step up if
message quality proves insufficient in practice.

**GitHub Copilot:**

```text
Endpoint:      https://api.githubcopilot.com/chat/completions
Model:         gpt-4.1              (Copilot's base model; aiModel overrides)
max_tokens:    150
stream:        true
```

For the user who already pays for Copilot and does not want a second bill. It is the **only
provider whose stored credential is not what gets sent**: the GitHub OAuth token buys a
short-lived token from `https://api.github.com/copilot_internal/v2/token`, and only that one
reaches the completion endpoint. `CopilotToken` caches it until two minutes before it expires.

Three things about this are worth knowing before it is extended or relied on:

- **The wire format is Chat Completions**, `choices[0].delta.content` — a third reader, not a
  reuse of the Responses API's. An empty `choices` array is the ordinary first frame, carrying
  only content-filter results, and a reader that treats it as a fault fails every request.
- **`Copilot-Integration-Id: vscode-chat` is required**, along with `Editor-Version`. Without
  them the endpoint answers 400 with an empty body, which reads exactly like a bad model name.
  The id has to be one GitHub has issued and there is no registration open to a per-user tool,
  so FlickGit sends an editor's. That, and `copilot_internal`, make this **an undocumented API
  that GitHub may change or close without notice** — which is why the default provider is still
  Anthropic and why a Copilot failure has to degrade like any other.
- **A personal access token does not work**, and a *fine-grained* one with "Copilot Chat" and
  "Copilot Editor Context" granted does not work either — those permissions apply to the documented
  `/copilot/*` endpoints on `api.github.com`, not to `copilot_internal`, which issues no scope a PAT
  can be granted at all. The exchange wants the OAuth token an editor already holds, so the key
  prompt names `%LOCALAPPDATA%\github-copilot\apps.json`; this is the only provider whose prompt
  differs. **The 404 says so too**, because the prompt is not where the user finds out: a PAT is
  stored happily and only fails on the first generation, and "GitHub does not offer Copilot to this
  account" sent them to check a subscription that was never the problem.

The base model spends no premium request, which is why it is the default rather than a faster
tier: a default that 404s on some subscriptions is worse than a slower one that works on all of
them.

**Ollama (local):**

```text
Endpoint:      {aiOllamaUrl}/api/chat        default http://localhost:11434
Model:         aiModel — required, no default
options:       { "num_predict": 150 }
stream:        true
```

The reason to have it is the one the other three cannot answer: **nothing sent to it leaves the
machine**, so it is the only provider available to somebody whose policy forbids source code
reaching a third party. That is also why it needs no credential and asks for no consent — the
consent a stored key stands for is consent to send code to *somebody else*.

Four things about it differ in kind rather than in wire format, and each costs a branch somewhere:

- **No default model.** The other three offer a fixed catalogue, so naming the fastest tier is a
  safe guess. Here the set of models is whatever the user has pulled onto their own disk, so *any*
  guess 404s for most people — with an error about a model they never asked for. An empty `aiModel`
  is refused by name instead, and the message says to run `ollama list`.
- **Newline-delimited JSON, not SSE.** Each chunk is a complete JSON object on its own line: no
  `data:` prefix, no blank-line separator, no `[DONE]`. Ollama also exposes an OpenAI-compatible
  endpoint that would have reused the existing reader, and it was not taken — that endpoint is a
  translation layer, it cannot carry `options.num_predict` or `keep_alive`, and a shim is a second
  thing that can be wrong between us and the model. `LineDelimitedJson` is twenty lines.
- **The silence budget is two minutes, not eight seconds.** The budget measures silence rather than
  total duration, and a cold Ollama spends its first silence reading several gigabytes of weights
  off disk. Eight seconds would guillotine every first generation after a reboot, report it as "the
  provider stopped answering", and count it towards the tray warning. Two minutes is still a guard:
  a local server silent that long is not loading, it is wedged.
- **The warm-up loads the model**, where the hosted three open a socket. A chat request with an
  empty message list is Ollama's own "load and stop", and it is sent with `keep_alive: 10m` — the
  only request that sets it, so the user's own `OLLAMA_KEEP_ALIVE` governs everything afterwards.
  The handshake this replaces costs nothing on loopback; the model load costs tens of seconds, and
  paying it at logon is the difference between the first commit message of the day arriving in half
  a second and in half a minute. `AiOptions.WarmUpBudget` is 60 s for Ollama against 5 s for the
  rest.

**`aiOllamaUrl` is a setting because running the model on a bigger machine on the same network is
the ordinary reason to use Ollama at all**, and there would otherwise be no way to express it. It
is the one case where "local" stops being literally true, so `flick ai` says which of the two it
is — *diffs stay on this machine*, or *diffs are sent to the Ollama host named above* — rather than
printing a claim that has quietly become false.

**No latency row in the table below.** First token depends on the model, the quantisation and
whether there is a GPU, which is a property of the user's hardware rather than of this product.
`flick diag timings` still records `ai.firsttoken` and `ai.complete`, which is the number that
actually matters to somebody choosing a model.

**Cost is not a constraint.** At ten commits per day with a capped diff: roughly $1/month on
Haiku 4.5, $0.25/month on Luna. Design around **time to first token**, not tokens spent.

## Latency engineering

Ordered by impact. All four are required.

**1. Cap the diff — biggest lever.** Latency scales with input size, and a commit message
does not need the full diff.

```text
if (diff size <= 12 KB)   send the diff verbatim
else                      send a synthesised file summary
                        + the first 40 lines of each file's hunks
                        + a "[truncated]" marker per file
```

The summary is **synthesised from the `--numstat -z` counts already on `GitFileChange`**, never read
from `git diff --stat`: that is human-readable Git output, which "Coding Guidelines" forbids parsing
— and it saves a process on the path with the tightest budget in the product.

Hard ceiling **4,000 input tokens**. Above that, quality stops improving and latency keeps
growing. Exposed as `Max diff size`, defaulted low.

Always exclude from the payload: lock files (`package-lock.json`, `*.lock`), generated code,
minified assets, and anything the secret detector flagged.

**2. Stream.** The user perceives time to first token, not completion. With a capped diff the
first words land in roughly 300–500 ms even though the full message takes 600–900 ms. Render
tokens as they arrive. Combined with queued Enter, the practical wait is zero.

**3. Keep the connection warm.** The resident service holds a pooled HTTP/2 connection to the
provider. A cold TLS handshake costs 100–300 ms — a third of the budget, otherwise paid on
every request.

**Copilot's warm-up is two round trips, not one**, and the second is the expensive one: the token
exchange costs ~450 ms measured, so leaving it lazy put it inside the first generation's first-token
budget on top of the second `api.githubcopilot.com` already takes to begin answering. Its
`ProbeAsync` exchanges the token as well as opening the connection, and a refused exchange comes back
as an unreachable provider — which is how a stale stored token gets named by `flick ai` at startup
rather than by failing the first commit of the day.

```csharp
new SocketsHttpHandler
{
    PooledConnectionLifetime       = TimeSpan.FromMinutes(15),
    PooledConnectionIdleTimeout    = TimeSpan.FromMinutes(10),
    EnableMultipleHttp2Connections = true,
}
```

One cheap warm-up request at service start is fine. Do not poll to keep it alive.

**4. Constrain the output.** `max_tokens: 150` is a runaway guard. The real control is the
system prompt: the message and nothing else — no preamble, no code fences, no explanation.
Strip fences defensively anyway.

Prompt caching is not useful here: the diff is unique on every call and the system prompt is
too short to pay for itself.

## Provider abstraction

```csharp
public interface ICommitMessageGenerator
{
    IAsyncEnumerable<string> GenerateAsync(
        CommitContext context,
        CancellationToken cancellationToken);
}
```

Implementations: `AnthropicGenerator`, `OpenAiGenerator`, `CopilotGenerator`, `OllamaGenerator`,
`DisabledAiGenerator`.

Still no base class between them. What all four share is one function they call --
`AiEndpoint.StreamAsync` -- and what differs is exactly its arguments: the URL, the headers, the
request shape, the framing, the silence budget and which frame carries text. Two cannot delegate
outright: Copilot has to await its token before the request can be authorised, and Ollama has to
resolve a model that may not be configured at all.

The return type is a stream, not a `Task<string>` — streaming is a requirement, not an
option.

## Prompt

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

Input: the capped diff, changed file names, optionally the branch name. Never send the whole
repository.

**That text is the default, not the prompt.** It lives in `CommitPrompt`, and `PromptStore` puts it
in `%LOCALAPPDATA%\FlickGit\commit-prompt.md` the first time FlickGit runs, from where the user
owns it. What the file says is sent verbatim; HTML comments are stripped first, which is the one
piece of syntax in it and exists so the seeded header can explain itself without reaching the model.
A file with no prompt left in it is refused rather than sent — an empty system prompt does not fail,
it produces confident nonsense. **Deleting one resets it, it does not unbind from it:** the seed runs
on every launch, because "missing" is the only signal there is and a marker file would be state
nobody asked for, so the file comes back holding the built-in wording. The header says so rather than
promising otherwise. The cost is that a later build improving the built-in prompt does not reach an
install that already has one written — accepted, because on-demand seeding buys that back only by
making the feature undiscoverable, which is the failure the AI key already had.

**`aiConventionalCommits` is not consulted while that file exists.** The setting picks between the
two built-in variants; a file is the whole prompt, and appending a rule the user did not write to a
prompt they thought was final is exactly the surprise this exists to remove. `flick ai` says so when
both are set, because a setting that silently does nothing is otherwise unanswerable.

**There is a third file**, `changelog-prompt.md`, seeded and resolved by the same `PromptStore` on
the same terms. Its header carries the one rule the other two do not: do not put the length in it.
Brief and Detailed are chosen in the log window and reach the model as the payload's last line, so a
rule about length written into the prompt fights that box rather than replacing it — which is
**The changelog**'s argument, and the reason `PromptStore.ForChangelog` takes no argument where
`ForCommit` takes one.

**The payload is not templatable, and that is the boundary.** `AiContext.ToPromptText` and
`DiffPayload` decide what may leave the machine. A prompt file changes the instructions and can
never widen them, so this adds no privacy surface — and the seeded header names what is appended
underneath, because a user who does not know that cannot write a prompt that makes sense.

It is read on **every generation** rather than cached at startup, deliberately unlike
`ActionCatalog`: iterating on wording is the point, and a resident service that had to be restarted
between attempts would make it unusable. A kilobyte read costs microseconds on a path already
costing hundreds of milliseconds.

**Not `git diff --cached`.** That would be right in a tool that stages as the user ticks, and wrong
here: `CommitFlow` stages as its *first* step, at commit time, so when the popup asks for a message
the index is usually empty and `--cached` returns nothing at all. Use

```bash
git diff HEAD -M --no-color --no-ext-diff --no-textconv -- <ticked paths>
```

which is what the commit will actually contain. Staging early to make `--cached` true is the wrong
fix: pressing Esc would then leave the index mutated, which is exactly the silent change to the
user's repository the Safety Rules forbid.

The three `--no-*` flags are load-bearing against the user's own gitconfig: `color.diff = always`
would fill the payload with ANSI escapes, `diff.external` would replace it entirely, and a textconv
filter would spawn a process per blob on a latency-critical path.

A ticked **untracked** file contributes no content — it is in neither HEAD nor the index — so its
name goes in the file list and its content does not. `git diff --no-index` per file would fix that
at the cost of a process each, and it is Phase 5 if message quality ever suffers for it.

## Privacy and secrets

**A provider with a key stored for it is the consent.** There used to be a second switch in
front of that — `aiAllowDiffsToLeaveMachine`, off by default, with a one-time dialog behind it —
and it is gone, along with the settings checkbox that was its only working surface.

It was answering a question the user had already answered. Naming a provider and storing a key for
it is not an idle preference: the one thing an AI provider does here is write a commit message, and
the only way to write one is from the diff. So the switch gated the feature on a fact it had already
been told, and its default made every fresh install's message box silently empty with no visible
sign that anything had been skipped — the Generate button is hidden, not disabled, when the AI is
unusable.

Worse, it gated the dialog meant to *ask* the question. `CanGenerate` consulted the setting before
`CommitMessageService.StreamAsync` was ever reached, so the "shown once on first use" prompt could
only ever fire once the answer was already yes. It was unreachable code guarding a checkbox nobody
could be told to tick.

What remains is the honest version of the same duty: the provider is named in Settings, `flick ai`
states outright that the diff of the files being committed is sent to it, and choosing **nobody**
sends nothing. Everything below still holds, and is what the privacy rule now rests on entirely.

Run the secret detector before sending **and** before committing. Patterns: AWS keys, GitHub
tokens, generic API keys, private key blocks, connection strings, passwords. Never send
`.env`, credentials or private keys. On detection, warn and redact.

## Failure behaviour

The AI is an accelerator, never a dependency.

- Unreachable, invalid key, rate limited, or timed out (**hard timeout 8 s**): the message
  field becomes an ordinary editable box with a one-line notice. Commit and push stay fully
  available.

  **The 8 s measures silence, not total duration.** Every frame restarts it, so it guards a request
  that has stopped answering rather than capping a generation that is arriving healthily. It covered
  the whole stream once, and all three of that bug's symptoms were the same bug: a message longer
  than the budget was *cut off mid-word* at exactly eight seconds; the resulting
  `OperationCanceledException` was indistinguishable from the user pressing Esc, so it was reported
  as a cancellation and the truncation happened **in silence**, uncounted by the failure counter; and
  for Copilot the failed request then dropped the cached token, so the next generation paid the
  exchange again and was slower still. A stall now raises `AiUnavailableException` naming the
  provider, which is what makes it visible and what makes three of them reach the tray warning.

  Not unit-tested, deliberately: the assertion would be that nothing happens for eight seconds, and
  a test that has to wait out a timer is testing `CancelAfter`. Verified by generating against a real
  provider — `flick diag timings` carries `ai.firsttoken` and `ai.complete`.
- Log the failure reason. Never log the diff or the key.
- Three consecutive failures: persistent tray warning rather than failing silently on every
  commit.

## Speculative generation (opt-in, off by default)

The service may start generating **before** the trigger, when the foreground Explorer folder
resolves to a repository, the working tree is dirty, and the folder has been foreground for
more than 1.5 s.

This makes the message already present when the popup opens. It also means diffs leave the
machine without an explicit user action, so it is off by default, clearly labelled, and
automatically disabled when the provider is not local.

Cache generated messages keyed by diff hash so repeated triggers on an unchanged tree cost
nothing.

---

# Action Catalog

The context menu, the palette and the CLI must not each define their own list of operations.
Define actions once, project them into every surface.

```text
                 ┌──────────────────┐
                 │  Action Catalog  │   built-ins + actions.json
                 └────────┬─────────┘
          ┌───────────────┼───────────────┐
          ▼               ▼               ▼
   Explorer menu     Repo palette        CLI
   (registry sync)                   (flick <id>)
```

```csharp
public sealed record GitAction
{
    public required string Id { get; init; }          // "commit", "custom.fetch-prune"
    public required string Label { get; init; }       // already localised
    public required ActionRun Run { get; init; }
    public string? IconFileName { get; init; }        // a name inside icons\, never a path
    public ActionSurfaces Surfaces { get; init; }     // [Menu] [Palette]
    public bool RequiresRepository { get; init; }
    public bool RequiresConfirmation { get; init; }   // forced on for anything destructive
    public ActionOutput Output { get; init; }         // toast, window, none
    public ActionParameter Parameter { get; init; }   // none, branch
    public int MenuOrder { get; init; }
    public bool InMoreSubmenu { get; init; }          // one level only, per Windows 11
    public bool Hidden { get; init; }                 // built-ins are hidden, never deleted
    public bool IsBuiltIn { get; init; }
    public string? Cli { get; init; }                 // the verb spelling, for a built-in
}
```

**One requirement flag, not six.** This section originally sketched `repo`, `notRepo`, `remote`,
`upstream`, `dirty` and `submodules`. Five of them were set by nothing, and the companion type that
carried the answers had to read `.git/config` to fill in a field nobody asked about — so all of it went
and the one distinction anything actually draws stayed. The registry context menu cannot evaluate even
that one, since a verb is written once and shown on every folder; it is honoured by the command line,
which refuses with a reason, and by `IExplorerCommand::GetState` in Phase 6. Add the others back when
a surface exists that can act on them.

**There is no `Cli` surface flag** for the same reason: the command line reaches an action by verb
(`flick commit`) or by id (`flick run custom.x`) rather than by asking the catalog what to offer, so
nothing would read it.

`IconFileName` is a bare file name rather than a path on purpose: the directory is resolved beside the
running executable, so a value from `actions.json` cannot name a location outside it.

`RequiresConfirmation` travels in one direction only. `ActionSafety` turns it on for anything on the
**Safety Rules** list and for every `ProcessRun`; nothing in the file can turn it off. A user action
that runs `reset --hard` from the palette with no confirmation is exactly the hole a "trust the file"
reading would leave — the user wrote the file, so they may have the command, but they do not get to
have it silently.

**A built-in's id is its CLI verb**, which is what makes `flick commit` and the Commit action the same
code path. A user action has no verb, so the context menu reaches it as `flick run <id> "%V"` — the
one thing a registry verb can be is a command line.

`ActionRun` variants: `WindowRun` (open a FlickGit window), `GitRun` (git with an argument
list), `ProcessRun` (external executable), `CompositeRun` (ordered sequence, stop on first
failure).

Built-ins ship in code and can be hidden or reordered, never deleted. User actions live in
`actions.json`:

```json
{
  "id": "custom.fetch-prune",
  "label": "Fetch (prune)",
  "icon": "icons/fetch.ico",
  "run": { "type": "git", "args": ["fetch", "--prune"] },
  "surfaces": ["menu", "palette"],
  "requires": { "repo": true, "remote": true },
  "menuOrder": 45,
  "showOutput": "toast"
}
```

Placeholders: `{repo}`, `{branch}`, `{upstream}`, `{remote}`, `{selection}`, `{files}`.
Substituted into `ArgumentList` entries — **never** into a concatenated string.

Security: `actions.json` can launch arbitrary processes. It lives in the user's own
`%LOCALAPPDATA%`, inside the existing trust boundary, but the settings UI must warn clearly
when creating a `ProcessRun`, and the file must never be importable from a URL or a
repository without explicit confirmation.

---

# Repository Palette

Secondary surface, for when the user is not in Explorer. Global hotkey, default
**`Ctrl+Alt+R`**.

**Not `Ctrl+Alt+G`**, which this document also names for the Explorer trigger. Two
`RegisterHotKey` calls for one combination cannot both succeed -- the second fails with
`ERROR_HOTKEY_ALREADY_REGISTERED` -- so the trigger keeps it, being the product's named feature and
the one whose section argues the choice through. The palette takes `Ctrl+Alt+R`, for repositories,
settable as `paletteHotkeyGesture`.

Because the user works across many repositories, the palette opens on **repositories that
have something to do**, not on a command list:

```text
┌──────────────────────────────────────────────────┐
│ >                                                │
├──────────────────────────────────────────────────┤
│ ● d360-portal        3 modified      ↑2          │
│ ● bookmeta           1 modified                  │
│ ● unical-api         clean           ↓4          │
│   oceaview           clean                       │
├──────────────────────────────────────────────────┤
│ ⏎ commit   Ctrl+⏎ pull --rebase all              │
└──────────────────────────────────────────────────┘
```

Typing filters repositories; a space or `>` switches to action mode for the selected
repository. Second-token completion comes from the action's declared parameter kinds:

```bash
git for-each-ref --format=%(refname:short) refs/heads refs/remotes   # branches
git tag --list                                                        # tags
git remote                                                            # remotes
git stash list                                                        # stashes
```

Rules:

- Render from cache **synchronously on open**, then refresh asynchronously and update in
  place. Never wait on a `git` process before showing.
- Cache TTL 5 s, invalidated on any write the tool performs
- Scan repositories in parallel, always with `--no-optional-locks`
- Repositories with thousands of refs must not be enumerated on every keystroke
- Subsequence fuzzy matching (`cnb` → `commit-new-branch`), scored by contiguity,
  word-boundary hits and MRU rank
- The exact command about to run is shown in the footer before Enter
- `Esc` closes with no side effects

Target: hotkey to painted palette **80 ms**.

Enable `core.fsmonitor` guidance: suggest it in `diag doctor` for large repositories, where
it takes `git status` from ~300 ms to a few milliseconds on Windows.

---

# Shell Integration

Every shell surface is a thin trigger. None contains logic. All launch `flick.exe`.
Surfaces can be added or removed without touching anything else.

## 1. Registry verbs — classic menu (Phase 1)

The everyday actions are **root verbs**, not submenu items. TortoiseGit's layout, for
TortoiseGit's reason: a submenu costs a hover and a second aim, and paying that on the action
performed all day in order to tidy up the seven that are not is the wrong trade.

```text
HKCU\Software\Classes\Directory\shell\FlickGit.10.commit
    MUIVerb                = "Commit / Push..."
    Icon                   = "<install>\icons\commit.ico"
    CommandFlags           = dword:00000020        ; ECF_SEPARATORBEFORE
    ExplorerCommandHandler = "{F1C7A6D2-3B84-4E5A-9C61-7D2E8A4B5C10}"
    \command  (default)    = "<install>\flick.exe" commit "%V"

HKCU\Software\Classes\Directory\shell\FlickGit.zz.menu
    MUIVerb                = "FlickGit"
    Icon                   = "<install>\FlickGit.exe,0"
    ExtendedSubCommandsKey = "FlickGit.Menu"
    CommandFlags           = dword:00000040        ; ECF_SEPARATORAFTER

HKCU\Software\Classes\FlickGit.Menu\shell\110switch
    MUIVerb                = "Branches..."
    Icon                   = "<install>\icons\branch.ico"
    \command  (default)    = "<install>\flick.exe" switch "%V"
```

- Per-user install only. No administrator rights.
- Register both `Directory\shell` and `Directory\Background\shell`. Background uses `%V`.
- `ExtendedSubCommandsKey` resolves relative to `HKCR`, i.e. `HKCU\Software\Classes\FlickGit.Menu`
- **No `Position` value.** This document used to require `Position = "Bottom"`, on the grounds that
  it put the entries "with the other tools' verbs at the end of the menu" and that it was "where
  TortoiseGit is". Both halves were wrong, and the registry of any machine with a few developer
  tools on it says so:

  - **Nothing else sets it.** `git_gui`, `git_shell`, `cmd`, `Powershell`, `WSL`, `vscode` — each
    registers a plain `Directory\shell` verb with no `Position` at all, and they all land in the
    block immediately above `New`. `Bottom` is what moved FlickGit *past* `New`, down beside
    `Properties`, which is further than "the end of the menu" was meant to mean.
  - **TortoiseGit is not a static verb.** It registers an `IContextMenu` handler under
    `Directory\Background\shellex\ContextMenuHandlers\TortoiseGit`, which is handed the menu
    itself and inserts into it at an index of its choosing. That is how it sits immediately above
    `New`, and no `Position` value can imitate it.

  The risk the value was guarding against — landing above Explorer's own `Open` — does not exist:
  `Open` is the default verb and is drawn first whatever else is registered. So the default
  placement is the correct one, and it is the one every other developer tool already uses.

  **Explorer decides the order within that block.** A static verb cannot ask for an exact index, so
  "immediately before `New`" is where the block happens to be rather than something that is being
  requested. If it ever has to be exact, the answer is TortoiseGit's — an `IContextMenu` handler on
  top of the DLL that already exists for `IExplorerCommand` — and not a `Position` value.
- Entries are ordered **alphabetically by key name**, on both levels. Prefix with a numeric
  stride (`10`, `20`, `30`) so reordering does not require rewriting every key. The submenu's
  own verb is `zz.menu` rather than a number, so it sorts after every root entry whatever
  stride the catalog gave one.
- **Every key the tool creates is named `FlickGit.*`.** The root entries are several keys now,
  so an uninstall finds them by enumerating that one prefix -- which is what keeps "never
  modify keys the tool did not create" structural rather than a promise.
- **Static verbs are gone. The block is a `ContextMenuHandler` and nothing else** — see **The
  context menu is a handler** below, which is the third and final answer to a question this document
  got wrong twice. What the verb layout above records is why it could not stay: the placement it
  reaches is the wrong block, and `MUIVerb` is a static string, so the branch in the Commit label and
  hiding an entry outside a repository are both impossible from it. They were kept for a while as a
  fallback for a `dotnet build` working tree, which has no Native AOT DLL; that bought a developer
  convenience and cost a second write path, a second read-back and a second shape for "is it
  installed" — which is what left the settings checkbox unticked on a working install. `Install`
  now refuses when the DLL is missing and says to publish. `Uninstall` still removes verbs an
  earlier version wrote.
- **The block is bracketed by separators**, via `CommandFlags = 0x20` (`ECF_SEPARATORBEFORE`) on the
  first entry and `0x40` (`ECF_SEPARATORAFTER`) on the last. Bars, but in the wrong block — see
  below.
- **`MUIVerb` is a static string**, written once and rendered for every folder on the machine.
  Nothing of ours runs while Explorer builds the menu, so a registry verb cannot know the branch,
  the repository, or anything else about what was clicked. The two root entries get around this
  with an `ExplorerCommandHandler` — see below — and everything in the submenu does not.
- Ship `.ico` files with 16, 20, 24, 32 and 48 px frames; Explorer picks by DPI
- Classic menu icons are **not** theme-aware. Use mid-tone outline glyphs that read on light
  and dark rather than shipping two sets.
- Support a flat-at-root layout as a setting, not only the submenu

### The context menu is a handler

**A static verb cannot be put where every Git client sits, and three attempts established that.**
The order Explorer draws the classic menu in is:

```text
Open, Open in new window, …            canonical verbs
Open with Code, Git GUI Here, …        Directory\shell static verbs
TortoiseGit, Sharing, …                shellex\ContextMenuHandlers
New
                                       Position="Bottom" static verbs
Properties
```

A verb reaches exactly three of those places — `Top`, the default, and `Bottom` — and none of them
is the handler block:

1. `Position = "Bottom"` put the entries **past `New`**, down beside `Properties`.
2. Removing it put them **up among the other tools**, which is the block above the handlers.
3. `CommandFlags` separators drew the bars, but around the entries *in that same wrong block*.

The handler block is not addressable by a verb at any setting. So the block is now an
`IContextMenu` handler — `FlickGit.Shell.dll`, registered under
`Directory\shellex\ContextMenuHandlers\FlickGit` and the same for `Directory\Background` and
`Drive`. That is what TortoiseGit is, and it is the only reason it sits where it does.

The handler contributes the whole block at once, which makes it **simpler** than what it replaced
rather than more complex:

- **One CLSID, not one per verb.** The `IExplorerCommand` handlers were one class per entry, each
  asked separately about its own title and state, because each hung off its own static verb.
- **`GetTitle` and `GetState` fold into `QueryContextMenu`.** The branch goes in the label as the
  item is inserted, and a repository-requiring item outside a repository is simply not inserted.
- **The whole `IObjectWithSite` chain is gone.** A click on a folder's background needed
  service provider → shell browser → active view → folder view → persist folder → PIDL → path, six
  hops each able to fail. `IShellExtInit::Initialize` is handed the PIDL.
- **The separators are drawn, not requested.** `InsertMenu(… MF_SEPARATOR | MF_BYPOSITION …)` either
  side, which is exactly what TortoiseGit's `QueryContextMenu` does and all it does about placement.

Two costs, both real:

- **Icons must be built by hand.** `InsertMenu` takes text and nothing else, so an `.ico` has to be
  loaded and drawn into a 32-bit top-down DIB and attached with `SetMenuItemInfo`. A menu bitmap
  without an alpha channel renders transparent pixels as black squares, which is worse than no icon
  — hence `CreateDIBSection` plus `DrawIconEx`, not `CopyImage`.
- **The DLL is required, not preferred.** Registering both layouts would be the menu twice over, and
  keeping the loser as a fallback was the bug above, so `Install` refuses without
  `FlickGit.Shell.dll` beside `flick.exe`.

**On Windows 11 all of this appears only under "Show more options" (Shift+F10).** The primary menu
needs the sparse MSIX package, which is still open; the global hotkey is the real fast path anyway.

## 2. IExplorerCommand + sparse MSIX (Phase 6)

To appear in the Windows 11 primary menu, implement `IExplorerCommand` and register it via a
sparse MSIX package with a `windows.fileExplorerContextMenus` extension. Registry verbs
cannot reach that menu, regardless of how they are written.

This also unlocks:

- `GetState` → return `ECS_HIDDEN` outside a repository, making the menu genuinely
  repository-aware
- `GetIcon` → icons in the modern menu
- `IEnumExplorerCommand` → a dynamic submenu built from the Action Catalog

Constraints:

- The DLL loads into `explorer.exe`. **No Git logic, no AI SDK, no WPF, no HTTP client.**
- `GetState` is called synchronously while the menu is built. Answer from a cache owned by
  the resident service within **20 ms**, hard timeout **50 ms**, falling back to "show"
  rather than blocking.
- Windows 11 accepts only **one level of submenu**
- Icons are declared in the package manifest as scaled assets, not as an `.ico` path
- Requires package identity and a signature. Self-signed works for internal use; Azure
  Trusted Signing or a standard code-signing certificate for distribution.

## 3. Not available — do not attempt

- **Explorer toolbar or ribbon buttons.** No public extension point, Windows 10 or 11.
- **Deskbands / Explorer bars.** Deprecated since Windows 10 1809.
- **A "git branch" column in Details view.** `IColumnProvider` is dead and property handlers
  do not apply to folders.

## 4. Status overlay icons — deliberately out of scope

The green tick / red exclamation badges use `IShellIconOverlayIdentifier`. The trade-offs are
bad:

- Registration is under `HKLM`, requiring **administrator rights** and breaking the per-user
  install
- Windows loads only the first ~15 handlers, sorted alphabetically. OneDrive, Dropbox and
  existing Tortoise clients already consume most slots — hence their space-prefixed names, an
  arms race not worth joining.
- The handler is called synchronously for every visible item. Any per-file `git status` would
  make Explorer unusable. It needs a maintained status cache with an immediate "unknown" on
  miss, which in turn needs working-tree watching, which is costly at scale.

**Not in scope through Phase 6.** If revisited, restrict to a single overlay on repository
root folders — one handler, one slot, no per-file tracking.

---

# Installer

`src/FlickGit.Setup` builds `FlickGit-<version>-x64.msi`. It exists for **one file**:
`FlickGit.Shell.dll` is loaded into `explorer.exe` and `DllCanUnloadNow` returns `S_FALSE`
forever, so from the first right-click the DLL is locked for as long as Explorer lives. Unzipping
over it fails, and the fix — kill Explorer, copy, re-register, restart Explorer — is a *sequence*,
which is the one thing an archive cannot be.

```text
close FlickGit -> close Explorer -> replace files -> register -> start Explorer -> start FlickGit
```

**Per-user, no elevation.** The registry entries are `HKCU`, the settings are in `%LOCALAPPDATA%`
and the autostart is a per-user logon task; a per-machine package would write one user's `HKCU`
keys and be wrong for everybody else. Installs into `%LOCALAPPDATA%\Programs\FlickGit`, the path
the README already recommended. Updating is running the newer MSI — one `UpgradeCode`, forever, and
`MajorUpgrade`.

**Registration goes through `flick.exe`, not through MSI registry rows.** The context menu is a
projection of the Action Catalog, so what the keys should say depends on the user's `actions.json`.
An installer cannot know that, and a second implementation of it would be a second answer to the
same question. So `install-shell`, `uninstall-shell`, `autostart on` and `autostart off` are custom
actions, and there is exactly one piece of code that writes those keys.

## The sequence is the package

Eight things decide where each action sits, and every one of them is a bug if it is got wrong.

- **The kills are immediate, and scheduled before `InstallValidate`.** That is where Windows
  Installer decides whether a file it is about to replace is in use, and it runs *before* the script
  that copies anything. A deferred kill would land after the user had already been offered a reboot
  for a lock we were about to remove deliberately.
- **`MSIRESTARTMANAGERCONTROL` is deliberately not set.** It was `Disable` for a while, on the
  reasoning that the kills above leave the Restart Manager nothing to find. They do — RM's
  detection runs *inside* `InstallValidate`, which both kills precede, so it prompts about
  nothing — but `Disable` does not mean "skip the check". It means "use the pre-Vista code path",
  which snapshots every running process and walks its module list in-process. On a developer
  machine that is **four to five minutes** of one core in kernel mode, with the progress dialog
  stuck on "Validating install". Measured at 4m 49s with the property against 10s without it,
  same package. The fast path is the default one, and the default is to say nothing.
- **`MSIFASTINSTALL` is 7, and bit 2 is why.** Removing the property above fixed one five-minute
  "Validating install" and left another standing, because `InstallValidate` has a second job:
  it walks every logical drive and asks each how much space is free, to prove the install fits.
  That includes the mapped network drives, and a mapped drive whose server resolves but does not
  answer costs an SMB timeout apiece in a call nothing can cancel — four of them on a corporate
  laptop is the same five minutes under the same progress text, on the machines where the share is
  unreachable and nowhere else. Which is why it survived a fix that measured 10s: the measurement
  was taken off the corporate network, where the same lookups fail instantly.

  Bit `2` skips that check. Bit `1` skips the System Restore point, which a per-user install into
  `%LOCALAPPDATA%` writing `HKCU` has nothing to put in; bit `4` reduces progress messages, which is
  free once the phase they were reporting is gone. The space check is worth nothing here in any
  case — it is three megabytes being weighed against three hundred gigabytes.
- **`install-shell` is deferred**, for the mirror-image reason. An immediate action scheduled
  `After="InstallFiles"` still runs before a single file exists, because the copying happens inside
  `InstallFinalize`. Only a deferred action runs in file order.
- **Every deferred action is `Impersonate="yes"`, without exception.** A deferred action that does
  not impersonate runs as **SYSTEM** even in a per-user install with no elevation anywhere — so
  `install-shell` would register the menu into SYSTEM's hive and report success.
- **The starts are immediate and go through `Start-Process`.** Nothing can be sequenced after
  `InstallFinalize` inside the script, and MSI *waits* for an exe action to exit — so an action that
  ran `FlickGit.exe` directly would hang the installer until somebody quit the tray icon.
- **FlickGit is started five seconds after Explorer**, and that delay is load-bearing. The resident
  service's first act is to add its tray icon, and `Shell_NotifyIcon` fails while the notification
  area does not exist yet — which is Explorer's state for a second or two after it starts. Launched
  immediately, FlickGit died on `TryCreate failed` on *every* install: no tray icon, no pipe, no
  resident service, and an installer that had reported success.
- **Nothing runs twice during an upgrade.** `MajorUpgrade` removes the old product inside the new
  one's transaction, which runs the old package's sequence too — so without `NOT
  UPGRADINGPRODUCTCODE` the outgoing package would unregister the menu and put a live Explorer back
  in front of the file copy that is about to replace its DLL.

Two smaller ones, in the same spirit: an exe action's working directory is its `Directory`
attribute, so the kills and the Explorer restart use `SystemFolder` — on a first install
`INSTALLFOLDER` does not exist yet and on an uninstall it is already gone, and an action whose
working directory is missing fails to start at all, silently, under `Return="ignore"`. And
`install-shell` is the only action allowed to fail the install: a package that copied the files and
quietly did not register the menu is the exact failure this thing exists to prevent.

## Version, ICEs, prerequisite

**Three fields, not four.** Windows Installer compares only `major.minor.build` when deciding
whether one package upgrades another, so the commit count the rest of the build carries is invisible
to it — two builds off one tag would look like one product version and the second would install
*beside* the first. `build.yml` therefore computes a separate `msiversion`: the tag on a tag build,
`0.0.<commits>` otherwise, deliberately not derived from the nearest tag so a `main` build cannot
pass itself off as the release it came after. `AllowSameVersionUpgrades` covers the rest.

**Four ICEs are suppressed and each is argued with in the csproj.** ICE38, ICE64 and ICE91 are all
the same objection — a per-user install into the user's profile — from a model in which every package
has a per-machine variant. ICE61 fires *because* of `AllowSameVersionUpgrades`. Nothing else is
suppressed, and ICE03 is why: it caught a `CustomAction.Target` over the 255-character limit, which
would have been a truncated command line nobody would have noticed.

**The .NET Desktop Runtime is checked with a directory probe**, not a registry search, and that is
forced rather than chosen: the .NET installer records what it installed as registry *value names*
(`9.0.19`), so nothing can ask "any 9.x". The probe catches the machine with no desktop runtime at
all, which is the case worth catching; one that has only .NET 8 falls through to the .NET host's own
dialog, which names the exact download.

## Not tested, and that is the rule

Hard Requirement 4 puts everything outside `FlickGit.Core` out of scope, and an installer is the
clearest case of it: what could be asserted is the content of the MSI tables, and what actually
breaks is the *order* — which only shows up by installing. So it is verified by running it, and the
verbose log (`msiexec /i … /l*v`) is the artefact to read: every custom action's placement and exit
code is in it. The runs worth doing are a first install, an upgrade over a version whose DLL Explorer
has already loaded, an uninstall (which must leave `%LOCALAPPDATA%\FlickGit` alone), and both of
those silently with `/qn`.

---

# Clone

Shown when the right-clicked folder is **not inside a repository**. This is the only action
available in that state, and it must be the default rather than `git init`.

```text
┌─ Clone into C:\dev\ ───────────────────────────────┐
│                                                     │
│  URL  [ https://dev.azure.com/org/proj/_git/repo  ] │  ← prefilled from clipboard
│  Into [ repo                                      ] │  ← derived from the URL
│                                                     │
│  ☑ Initialise submodules                            │
│  ☐ Shallow clone (--depth 1)                        │
│                                                     │
│  [ Clone ]                              [ Cancel ]  │
└─────────────────────────────────────────────────────┘
```

## Clipboard prefill

The single biggest time saver here. The user copies the URL from Azure DevOps or GitHub,
right-clicks, and the field is already filled.

On open, read the clipboard once and prefill **only if** the content matches a Git remote
shape: `https://`, `http://`, `ssh://`, or `git@host:path`, ending in `.git` or matching a
known forge path pattern. Anything else leaves the field empty. Never execute a clone from
the clipboard without the user pressing Clone.

The target directory name is derived from the last URL segment with `.git` stripped, and
stays editable.

## Target directory

The right-clicked folder does **not** need to be empty. `git clone` fails into a non-empty
directory, so always clone into a **subdirectory** of the right-clicked folder, named from
the URL. If that subdirectory already exists and is non-empty, say so and let the user
rename before proceeding.

## Command

```bash
git clone --progress [--depth 1] [--recurse-submodules] -- "<url>" "<dir>"
```

Prefer `--recurse-submodules` over a separate `submodule update` pass — it clones submodules
in parallel with the main history and is meaningfully faster.

## Progress and cancellation

`git clone --progress` writes progress to **stderr**, not stdout. Parse it for the phase
(counting, compressing, receiving, resolving, checking out) and the percentage, and show a
determinate progress bar.

Cancellation must kill the process tree **and delete the partial target directory**. A
half-cloned directory that looks like a repository is worse than no directory. Only delete a
directory the tool created in this operation — never a pre-existing one.

## Authentication

Do not implement credential handling. Let Git's credential helper do its job. If the clone
fails on authentication, show Git's own stderr and suggest `git credential-manager` — never
prompt for a password inside FlickGit, and never store one.

---

# Submodules

Submodule support is conditional, not automatic. Running submodule commands on repositories
that have none wastes time on every single action.

**Detection:** check for the existence of a `.gitmodules` file at the repository root. This
is a file-system check costing microseconds, not a Git invocation. Cache it with the
repository root. Never run `git submodule status` just to find out whether submodules exist.

The `hasSubmodules` requirement gates every submodule-related action and menu entry.

## Update after pull

```bash
git submodule update --init --recursive
```

Runs **after** a successful `pull --rebase`, only when `.gitmodules` exists. If the pull
fails or stops on conflicts, do not touch submodules.

Show it as a distinct step in the progress dialog — it hits the network and can take
noticeably longer than the pull itself:

```text
Pull --rebase        ✓  2 commits
Update submodules    ⟳  3 of 7
```

A submodule failure does **not** roll back the pull. Report it separately: the pull
succeeded, the submodules are stale, here is the error.

## The window

Beside **Branches** and **Tags** in the submenu, and one screen for the same reason: what is there,
add one, remove one — all three of which begin with "what is there already". Reached from the
FlickGit submenu, the palette and `flick submodule <path>`.

```text
┌─ Submodules — d360-portal ─────────────────────────────────────────┐
│ >                                                                  │
├────────────────────────────────────────────────────────────────────┤
│ libs/protocol    git@github.com:acme/protocol.git      changed     │
│ vendor/spdlog    https://github.com/gabime/spdlog.git              │
│ third/asio       https://github.com/chriskohlhoff/asio  not init'd │
├────────────────────────────────────────────────────────────────────┤
│ ADD A SUBMODULE                                                    │
│ URL  [                        ]   Into [ libs/protocol ]  [ Add ]  │
├────────────────────────────────────────────────────────────────────┤
│ Added libs/protocol.  Staged, not committed. [ Commit… ] [ Close ] │
└────────────────────────────────────────────────────────────────────┘
```

**It commits nothing, and that is the point.** `submodule add` and `git rm` both leave their work in
the index, and the window stops there: it says *staged, not committed* and its button opens the
commit window. **Commit Window** is the only commit surface, so a message box here would be a second
place for the primary-branch warning, the staging defaults and the push guardrails to live — and the
staged `.gitmodules` and gitlink show up in that window as ordinary ticked rows with no work at all.

## Two reads, and `submodule status` is not one of them

```bash
git -C <repo> --no-optional-locks config -f .gitmodules --list -z
git -C <repo> --no-optional-locks diff HEAD --name-only -z --ignore-submodules=none -- <paths>
```

**`git submodule status` has no `--porcelain`**, so its output is the form shaped for a terminal and
**Coding Guidelines** forbids parsing it. What it would have given comes from three cheaper places
instead, and each one is exact:

- **`.gitmodules`, through the parser the repository's own config already goes through.** That is
  what `GitConfigList` is: `ParseList` and `SubsectionOf` moved out of `RepositoryConfigService` when
  the second caller arrived. It is also the only source that lists a submodule **nobody has
  initialised yet** — which is the row most worth showing, being the one with something to do.
- **Initialised is `File.Exists(<path>/.git)`**, a probe costing microseconds, the same rule this
  section already sets for `.gitmodules` itself. No Git call, no process.
- **Changed is one `diff HEAD`.** Against HEAD rather than the index, so a pointer the user has
  already staged still reads as changed: "you updated this, commit it" is the question, and staging
  is half an answer to it. `--ignore-submodules=none` because a user's own `diff.ignoreSubmodules`
  would otherwise silently empty the column.

The name trap is `remote.*`'s, and worse: **a submodule's name defaults to its path**, so
`libs/proto.v2` is a subsection with a dot in it. The name is everything between the first separator
and the last, never the second field.

## Removing asks twice, and only the second answer forces

```bash
git submodule deinit -- <path>
git rm -- <path>
```

`deinit` first: `git rm` on a populated submodule works, but it is `deinit`'s refusal that names the
user's uncommitted work, and reaching `rm` first would have emptied the checkout the question is
about. `git rm` on a gitlink takes the `.gitmodules` entry with it, so there is no third command.

When Git refuses because the submodule holds work that was never committed, **that refusal gets its
own second question naming what is at stake**, and only an answer to *that* calls back with `-f` —
the shape `branch -d` and `branch -D` already have, and the only route to a forced spelling here.
`SubmoduleService` never escalates on its own; `force` is a parameter, and both commands take it
together, because `deinit -f` leaves a checkout `rm` would still refuse.

**`.git/modules/<name>` is never deleted, forced or not.** It is the submodule's own clone, and it
can hold commits made in there and never pushed — work the outer repository has never seen, which is
the one thing **Safety Rules** makes unconditional. The price is that re-adding the same submodule
later needs that directory cleared by hand, and the confirmation says so rather than doing it.

## Adding refuses before Git runs

`submodule add -- <url> <path>`, with every refusal answered first so the window can show it as a
hint while the user is still typing: no URL, no path, a path that is absolute or climbs out with
`..`, a target that exists and is not empty, and a path already declared. The path guard is
`WorkingTreeWriter.ResolveInsideRepository`, which is public for exactly this and catches the
absolute and the escaping case in one test.

The target folder is derived from the URL's last segment with `.git` stripped — **Clone**'s rule,
and Git's own — and stops being derived the moment the user types in the box.

## Deliberately out of scope

> **No committing inside a submodule, no changing a pointer by hand, no `--remote` updates, no
> deleting `.git/modules/<name>`, and no branch, sync or foreach.**

If the status shows a modified submodule pointer, it is displayed as a normal changed entry and the
user decides — which is what `--porcelain=v2` already produces with no special case, and what makes
committing an update work without this window at all.

**The menu entry is not gated on `hasSubmodules`.** `GitAction` carries only `RequiresRepository`,
and this window is where the *first* submodule is added — so hiding it in a repository with none
would hide the way in. A repository with no `.gitmodules` opens to an empty list that says so, the
way the tag window's does.

---

# Branches

Lives in the FlickGit submenu, and as `switch` in the palette and CLI. It is separate from the
commit-surface ComboBox, which only switches as part of committing.

**The label is "Branches…", the verb is still `switch`**, and the two disagree on purpose. The
window does three things — switch, create, delete, locally and on a remote — so "Switch branch…"
named one of them, and "Branches" beside "Tags" is what the two entries actually are: one screen
per kind of ref. The verb names the operation a command line performs, which is the one thing
`flick switch <path> <branch>` still does and the spelling `git switch` already has. Labels and
action ids diverge elsewhere for the same reason — `log` is "Show log…", `pr` is "Pull request…"
— so nothing structural changed with the name: three strings per `.lang` file, and the built-in's
id stayed `switch` because a built-in's id **is** its verb.

```text
┌─ Branches — d360-portal ────────────────────┐
│ > sto│                                      │
├─────────────────────────────────────────────┤
│   feature/storage-gw          (current)     │
│   feature/storage-cache                     │
│   origin/feature/storage-gw   (remote)      │
└─────────────────────────────────────────────┘
```

Fuzzy filter over local branches, with remote-tracking branches shown below and separated. A
remote branch creates a local tracking branch:

```bash
git switch --track origin/<branch>     # or: git switch <branch> when unambiguous
```

## Dirty working tree

```bash
git switch "<branch>"
```

Attempt the plain switch first. Git carries uncommitted changes across when there is no
conflict, which is usually what the user wants.

If Git refuses, **do not stash automatically.** Show which files block the switch and offer
three explicit choices:

```text
Cannot switch to main — these files would be overwritten:

  src/GatewayClient.cs
  src/Options.cs

[ Stash, switch, restore ]   [ Open commit window ]   [ Cancel ]
```

The stash path follows the same rules as the pull fallback: create a uniquely identifiable
stash, switch, restore **only that stash**, and never pop an unrelated one. If the restore
conflicts, stop and tell the user the stash still exists and how to reach it.

## Creating and deleting, from the same list

This window is the only surface in the product that creates or deletes a branch outside of a commit.

**Create is the filter box**, not a New button and not a second window: type a name that matches
nothing and the last row becomes *Create '<name>'*, in the accent colour. That is the gesture the
commit window's ComboBox already uses — "type a new name to create it" — so there is one way to name
a new branch in FlickGit rather than two. It creates from **HEAD**, per **Branch Selector**, not from
whichever row is highlighted: the list has meant "where do I want to go" up to that point, and making
the selection silently mean "and from here" would be a second meaning nothing announces. `switch -c`
runs only after `check-ref-format` has approved the name.

**Delete is a right-click**, on the row under the pointer — the `ListBox` is told to select it first,
because a menu built for the previously selected row deletes the wrong branch. What the menu offers
is what the row *is*, and an item that does not apply is absent rather than greyed:

| row | menu |
|---|---|
| the current branch | nothing — it is deletable by nothing, here or in Git |
| a local branch | **Delete branch…** |
| a remote-tracking branch | **Delete on `<remote>`…**, naming where it would land |
| the create row | nothing — there is no branch yet |

**`branch -D` is never reached by the window deciding to reach it.** A delete runs `branch -d`. When
Git refuses because the branch is unmerged, that refusal gets its own second question naming what is
at stake, and only an answer to *that* calls back with force. Two questions, neither remembered,
which is what **Safety Rules**' "explicit user intent, expressed in the moment" means for the one
entry on that list this surface can reach.

**Deleting on a remote is the only thing in FlickGit that destroys state other people share**, and
the only one with no local undo — so it is confirmed in its own words, saying so. It pushes
`refs/heads/<branch>`, never the bare name, for the reason `TagService.DeleteAsync` gives: `git push
origin --delete release` is ambiguous when a tag of that name exists, and here the wrong guess
deletes a tag, which has no reflog. The remote is resolved against the configured remotes rather
than split at the first slash — a branch may contain slashes, so `origin/feature/x` is only
resolvable by knowing `origin` is a remote. A prefix that is not one is refused rather than pushed
at. There is no force and no lease; `push --delete` removes the remote-tracking ref itself, so
nothing here prunes anything by hand.

---

# Tags

Beside **Branches** in the submenu, and one screen for the same reason: what exists, create one,
delete one, all three of which begin with "what is there already". `TagService` carries the rules —
the remote goes first in a deletion, nothing is ever forced, and no window open costs an
`ls-remote`.

## Checking one out

**Double-click a tag, or pick `Check out v1.4.0…` from the same right-click menu the deletion is
on**, and after one question HEAD is on that commit.

```bash
git switch --detach <tag>
```

**This is the only thing in FlickGit that detaches HEAD**, and that is why it asks. Everywhere else
the state is something to be *reported and refused*: `SwitchService.ListCandidatesAsync` drops
`origin/HEAD` from the picker rather than offer a row that would produce one, and both
`PushService` and `PullRequestService` stop with "HEAD is detached, so there is no branch to…".
Producing it deliberately in one place is a decision, so it is taken in words the user reads —
naming the state and naming the way back — rather than performed on a double-click and explained
afterwards.

`switch --detach` rather than `checkout`, on the rule the rest of the product follows: Git 2.23 is
the stated minimum, and the older spelling would be a second way to say the same thing.

Four consequences, each of which is where this could have gone wrong:

- **The question names the tag and no sha.** `GitTag.Target` is `%(objectname:short)`, which for an
  *annotated* tag is the tag object rather than the commit HEAD would land on. A number in a
  confirmation has to be the number the operation uses, so there is no number.
- **The row is the one under the pointer, not the selected one.** `ApplyFilter` selects index 0
  every time the list is rebuilt, so a double-click on the empty space below the last row would
  otherwise check out the newest tag in the repository from a gesture aimed at nothing.
- **A refusal offers no stash.** `git switch` refuses rather than overwriting, and the blocking
  files are reported with "nothing was modified or discarded" — but `StashSwitchRestoreAsync` is
  the Branches window's and cannot switch to a tag, so a button offering it here would be a button
  that cannot work. That is the same rule **Branches** applies to a refusal a stash cannot fix.
- **The window stays open and says so**: *"HEAD is detached at v1.4.0."* The branch picker closes on
  a successful switch; this one cannot, because the sentence naming the state is the whole reason
  the question was worth asking, and a window that closes cannot say it.

**There is no command-line spelling**, exactly as there is none for deleting a tag and for the
reason `VerbKind.Tag` already records: detaching HEAD needs intent expressed in the moment, and a
token in a script is the opposite of that. `flick tag <path> <name>` creates a tag and does only
that.

## What it deliberately does not do

> **No moving a tag, no `--force`, no signing, no tag-at-a-chosen-commit.**

A tag is created on HEAD, because the log window offers no action on a commit to pick one *from* —
which is a decision rather than a missing feature. Moving a published version number is the one
operation whose only implementation is `--force`, and there is none anywhere below this window.

---

# Context Menu Layout

The default projection of the Action Catalog. Everything here is reorderable and hideable in
settings; this is the shipped default, not a fixed structure.

**Folder not inside a repository** (`requires: notRepo`):

Nothing distinguishes this case on this surface. A registry verb is written once and shown on
every folder, so the same entries appear and the ones needing a repository refuse with a
reason when clicked. `Clone…` sits in the submenu for exactly that reason: there is nothing
here that could promote it. Repository-aware visibility needs `IExplorerCommand::GetState`,
which is Phase 6.

**Folder inside a repository** (`requires: repo`):

```text
… the rest of the Explorer context menu …
─────────────────────────────────────────
Commit / Push…
Pull (rebase)                       ← + submodule update when .gitmodules exists
FlickGit                          ▸
      ├── Show log…
      ├── Branches…
      ├── Tags…
      ├── Submodules…
      ├── Push
      ├── Pull request…
      ├── Repository settings…
      ├── Clone…
      ├── Fetch (prune)
      └── Open terminal here
```

**Folder or file** — the menu is one `IContextMenu` handler now, so it is asked once per click and
answers for the thing that was clicked. A right-clicked **file** gets its own, much shorter block:

```text
… the rest of the Explorer context menu …
─────────────────────────────────────────
FlickGit                          ▸
      ├── Blame…
      ├── Add
      └── Remove…
```

Nothing else applies to a file, and the folder entries are absent rather than greyed out. This is the
one thing a static registry verb cannot do — a verb is written once and drawn on every file on the
machine, repository or not — which is why the file surface is handler-only and has no static
fallback. `ActionSurfaces.File` is what puts an action here.

### Add and Remove

`git add` and `git rm`, on the one file that was clicked. `FileTrackingService` is the whole of it,
and the two verbs answer in text rather than in a window, so `flick add <file>` and `flick rm <file>`
are the same code path.

**Add stages the file**, which for one Git has never seen is what starts tracking it. Nothing to
confirm: staging discards nothing, and unticking the row in the commit window is how it comes back
out.

**Remove deletes the file and stages the deletion** — TortoiseGit's Delete, and a different operation
from the file list's **Deleting**, which sends the file to the Recycle Bin and runs no Git command
at all. Four rules make it safe enough to sit behind one question:

- **Nothing is forced.** `git rm` without `-f` refuses a file whose content differs from both HEAD and
  the index, so "never discard uncommitted work" is enforced by Git rather than by us. What is left
  is recoverable: HEAD still has the content, and **Reverting** puts it back.
- **An untracked file is refused before the question is asked**, because Git's own answer —
  `fatal: pathspec … did not match any files` — is accurate about a question the user did not ask.
  The exit code stays Git's, so a script branches on the same number either way.
- **It asks on every surface, and with a dialog even from the command line.** The same rule and the
  same reason as creating an upstream: the fast surfaces are not shortcuts around **Safety Rules**.
- **The pathspec cannot glob.** Everything after `--` is still a pathspec, so `a[1].txt` — an ordinary
  Windows file name — is read as a character class and matches `a1.txt` instead. Both commands pass
  `:(literal)<path>`, which is what makes one click delete exactly one file.

**Neither is on the folder menu.** `git add` on a directory stages everything under it and `git rm -r`
removes it, which is a blast radius a single click should not have — and there is no `-r` anywhere in
the service. A directory reaching `rm` from the command line is refused by Git, in words that name the
flag it would need.

Two entries in the context menu itself, because those are the two the user performs all day.
**Show log… was considered for a third and left in the submenu**, on that same rule: the root
entries are the two the user *performs*, not the one they read.
One click, from wherever the pointer already is. Everything else is one hover away inside the
**FlickGit** submenu, which sorts after them.

There is **no "More" entry** any more. It was a third level in all but name — a FlickGit root
entry, a submenu, and More inside that. Now the root entries *are* the menu and the submenu
*is* the overflow: one hop shorter for the frequent actions, the same for the rare ones.

Windows 11's `IExplorerCommand` accepts only one level of submenu (Phase 6), so the catalog
stays a flag (`InMoreSubmenu`) rather than a tree: never more than two levels.

`Commit / Push…` is a single entry, not two. The commit surface carries both buttons and the
branch ComboBox, so there is nothing left for a separate "commit in new branch" or "commit to
main" entry — both are reachable by typing in the ComboBox.

**There is no `Repository status…` entry, and `status` is not in the Action Catalog at all.** It was
in the submenu and in the palette, and it opened the **commit window** — because `flick status`
answers in text, a click has no console to print into, and `VerbRunner` fell back to a window. So the
seventh item in the submenu was the first item in the menu under a second name, differing in nothing:
same host, same pre-warmed window, same view model. `flick status <path>` stays, because printing the
file list for a script or a terminal is the one thing no other surface does — but the catalog is what
the menu and the palette are projections of, so a CLI-only verb does not belong in it. Per **Hard
Requirement 1** the entry and its six `action.status` language keys were deleted rather than hidden
behind `ActionSurfaces.None`, which would have been a row nothing reads.

---

# Interface Text

Every string the windows show comes from one `key = value` file per language, embedded in
`FlickGit.exe`:

```text
src/FlickGit.App/Languages/en.lang     de.lang  es.lang  fr.lang  it.lang  pt.lang
```

**Not `.resx`.** Satellite assemblies are DLLs in per-culture subdirectories, which is the
opposite of the layout everything else here is arranged around -- and a plain text file is
something a translator can open without Visual Studio and send back as a diff.

- `en.lang` is the master and the **per-key** fallback, so a half-finished translation shows
  English rather than raw key names.
- `@name` is the language's name for itself, never translated: someone looking for their
  language in an interface they cannot read is looking for the word "Francais", not for
  "French".
- Adding a language is adding a file. The csproj embeds `Languages/*.lang` by wildcard and
  `Strings` finds them by enumerating manifest resources.
- `WithCulture=false` on the `EmbeddedResource` item is load-bearing. Without it MSBuild reads
  `fr.lang` as a culture-specific resource and builds it into a satellite assembly, which is
  then never found at run time.
- `Strings.Use` must be called **before the first window is constructed**. Every view reads
  its text on construction and the resident service keeps instances alive for the whole
  session, so a language applied later never reaches them.

## Choosing one

`flick language` lists what is embedded and marks the one in use; `flick language fr` switches;
`flick language auto` goes back to following Windows. It writes `language` in `settings.json` and
says to restart, because `Strings.Use` runs once before the first window and the resident service
keeps those windows for the session -- a language applied to a live process would reach nothing.

The settings window carries the same picker, and the verb stays for the reason `flick autostart`
does: a script has no window to click in. Both read `Strings.Available`, which enumerates the
manifest resources rather than keeping a second list that could disagree with the files actually
shipped -- so neither surface can offer a language the exe was not built with.

An unknown code is refused with exit code 4 and the list, never silently ignored;
`diag doctor` names the requested code alongside the one actually in use, so "I set it to sv and
nothing changed" is answerable.

## Close, or Cancel

One word per meaning, and there are exactly two keys for it: `common.close` and
`common.cancel`.

**A button that only dismisses a window says Close.** The commit window, the pull-request
window, Settings, Branches, the log, blame, tags and the repository window all end in the
same gesture -- nothing is running, nothing is undone, the window goes away -- and for a while
four of them called it Cancel and four called it Close. A button promising to call something
off, on a window where there is nothing to call off, is a promise about work that was never at
risk.

**Cancel is for a button that actually stops something**, and the list is short: the
confirmation dialog's negative answer, declining to create an upstream, discarding the secret
typed into the key prompt, the clone window *while a clone is running* -- where it kills the
process and removes the partial directory, which is why that one button says Cancel then and
Close otherwise -- and the third answer in the Switch window's blocked strip, which declines
the switch rather than dismissing a window.

Per **Hard Requirement 1** the nine keys that used to hold these two words -- one per window,
six of them saying nothing but "Close" -- were deleted rather than left as aliases.

---

# Repository Settings

FlickGit's own settings are `flick settings`. **This is the other kind**: the repository's own, one
repository at a time, reached from the FlickGit submenu, the palette and `flick repo <path>`.

```text
┌─ Repository — d360-portal ─────────────────────────────────────────────┐
│ C:\dev\d360-portal                                                    │
│ IDENTITY                                                              │
│  ( ) Use the global identity — Thierry Quemerais <t.q@…>              │
│  (•) Set an identity for this repository                              │
│      Name  [ Thierry Quemerais ]   Email [ t.q@… ]                    │
│ REMOTES                                                               │
│  origin    https://dev.azure.com/org/proj/_git/repo         tracked   │
│  fork      git@github.com:o0Zz/FlickGit.git      push: ssh://…        │
│      Remote [ fork ]  URL [ … ]    [ Add ] [ Save remote ]  [ Remove ]│
│ FLICKGIT, FOR THIS REPOSITORY                                         │
│  Primary branch [ develop ]   empty resolves it from origin/HEAD      │
│  A new branch may create an upstream here.       [ Ask again ]        │
├───────────────────────────────────────────────────────────────────────┤
│ StatusText                                     [ Save ]    [ Close ]  │
└───────────────────────────────────────────────────────────────────────┘
```

It exists because two things a user needs constantly had no surface at all. **Which identity does
this repository commit as** — inherited from global config unless overridden, and the commit
window gives no hint, so getting it wrong is only visible afterwards in the log. And **where does
this push to** — nothing in the product ever showed a remote's URL: `PushService` reads the remote
*names* and picks `origin`, and that was the whole of remote visibility.

## One read, and no `git remote -v`

```bash
git -C <repo> --no-optional-locks config --local --list -z
```

returns the identity, every remote and the `flickgit.*` keys in a single process — so there is one
parser rather than two, and `git remote -v` is never parsed. That is output shaped for a terminal,
which **Coding Guidelines** forbids. Three more reads go out beside it, in parallel: `config --get
user.name` and `--get user.email`, which are what distinguish an override from an inheritance, and
`symbolic-ref --short --quiet HEAD`, which is what lets a remote row be marked *tracked*.

Two traps, both encoded with a comment saying why:

- **`config --list` lower-cases the section and the final component and leaves the subsection
  alone.** So `flickgit.primaryBranch` comes back as `flickgit.primarybranch` while
  `remote.MyFork.url` keeps its capitals. Every key is matched case-insensitively and a remote name
  never is — and since a remote may itself contain dots, the name is everything between the first
  separator and the last, not the second field.
- **`config --unset` exits 5 when the key was not there.** That is the ordinary answer for "use the
  global identity" on a repository that never overrode it, and reporting it would put a Git error in
  front of a user whose request was already satisfied. Exit 5 is success; every other non-zero exit
  is not.

## FlickGit's four per-repository keys

`flickgit.primaryBranch`, `flickgit.allowUpstreamCreation`, `flickgit.pullRequestTarget` and
`flickgit.forge`, in the repository's own config rather than in `settings.json`. The last two are
**Pull Requests**' — where a request goes, and which service hosts it when the hostname does not
say. Neither has a row in this window yet: both are refusals that name the `git config` line, which
is the surface a user reaches them through today. A path-keyed dictionary in a global file goes stale the moment the
repository is moved and is invisible from the place it applies; `.git/config` is neither, and it is
not committed, so nothing leaks into the repository's history. See **Persistence** for what that
replaced.

## Two save rules, on purpose

**A remote edit applies when its button is pressed**, the way creating a tag does: each one is a
single Git command with its own button, and **Remove** confirms first — it takes the
remote-tracking branches with it and leaves any branch that tracked it with no upstream, which is
more than the row the user is looking at. **The identity and the two defaults apply on Save**, the
way the settings window's do, because they are a form rather than a list of commands. The footer says
which is which.

A rename and a re-point in one press run **rename first**. The other order points the old name at
the new URL and then renames it — which works, until the rename fails and leaves a remote nobody
asked for pointing somewhere new.

## What it deliberately does not do

> **No network. No credentials. No global config. No `git init`.**

Nothing here fetches, runs `ls-remote`, or checks that a URL resolves — the next push answers
that, in Git's own words, and a window that took a round trip before letting a button be pressed is a
window nobody uses. An identity for *every* repository on the machine is `git config --global`'s
business. And per **Clone**, a folder that is not a repository gets the clone dialog, not this.

---

# Settings

`flick settings` opens a small window, and the tray's Settings and About entries open the same
one. Three tabs and nothing else:

```text
┌─ FlickGit — Settings ────────────────────────────────────┐
│ [ Settings ] [ Help ] [ About ]                          │
│                                                          │
│  EXPLORER                                                │
│  ☑ Show FlickGit in the Explorer context menu            │
│     Windows 11 keeps it under "Show more options".       │
│  ☐ Start FlickGit with Windows                           │
│     A logon task, 45 s after sign-in.                    │
│                                                          │
│  COMMIT                                                  │
│  ☑ Warn when committing to the primary branch            │
│  ☑ Close the commit window after a successful commit     │
│  ☑ Show a notification after a successful commit         │
│                                                          │
│  PULL                                                    │
│  ☐ Close the pull window after a successful pull         │
│                                                          │
│  COMMIT MESSAGES (AI)                                    │
│  Written by                                              │
│  [ Anthropic                                        ▾ ]  │
│    Disabled · Anthropic · OpenAI · Copilot · Ollama      │
│  [ Set API key… ]  [ Remove key ]                        │
│  A key is stored for Anthropic, in Windows Credential    │
│  Manager. The diff of the files you commit is sent to    │
│  this provider.                                          │
│                                                          │
│  LANGUAGE                                                │
│  [ English                                          ▾ ]  │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │ Everything else is configured in these two files:  │  │
│  │ %LOCALAPPDATA%\FlickGit\settings.json              │  │
│  │ %LOCALAPPDATA%\FlickGit\actions.json               │  │
│  │ [ Open configuration folder ]                      │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
│                              [ Save ]      [ Close ]     │
└──────────────────────────────────────────────────────────┘
```

**This is not the settings app this section used to specify**, and the reason the old one was
dropped still holds: its largest section was a drag-and-drop action list with per-row icon
pickers and an inline editor — more UI than Phases 1 to 4 together, and a graphical front end
for a file that is documented and hand-editable. `actions.json` remains the way to customise
the menu.

What that reasoning never covered is the handful of switches whose JSON key nobody can guess
before they have found the file: whether the Explorer menu is registered at all, whether the
tool starts with Windows, and which language this is. Those are worth a window. The rest says
where it lives and stops.

**The AI section is here for a stronger version of the same reason: the key was not in any file.**
`aiProvider` is a settings key somebody could find, but the key itself lives in Credential Manager
and the only way to store one was `flick ai key set` — a fine way to keep a secret and a hopeless
way to discover that you can. A user who has an API key and wants commit messages has no reason to
suspect a CLI verb exists, and the message box gives no hint, because a provider with no key is
indistinguishable from no provider at all.

**Ollama is the one entry that changes what the section means** rather than which service it points
at: the key button is disabled and the status line says so, because there is nobody to authenticate
to. It is also the one provider whose model is not optional, so that line names `ollama list`.

So: the provider, and a button that opens the existing key prompt. Two controls, no model picker
and no max-diff field — those are `aiModel` and `aiMaxDiffBytes` in `settings.json`, guessable once
the provider is on, and neither is a thing anyone changes twice.

**The picker names the service and nothing else** — `Disabled`, `Anthropic`, `OpenAI`,
`GitHubCopilot`. It used to name the model with it ("Anthropic — Claude Haiku 4.5"), which was a
second place for the default to be written down and wrong the moment `aiModel` was set to anything.
The model is `aiModel`: empty means the provider's default, and `flick ai` prints the one actually
resolved, which is the single answer to "what is it using". **No consent checkbox either**; see
**Privacy and secrets** for why the one that was here was removed rather than moved.

Rules the window follows:

- **Every value is read from its source of truth on open** — the registry for the context menu,
  the Task Scheduler for autostart. Never from a remembered flag: `flick uninstall-shell`, a task
  deleted in the Scheduler, or a hand-edited registry all happen outside this window, and a
  checkbox that disagreed with the registry would be worse than no checkbox.
- **Nothing is applied until Save**, and Save touches the registry or the Task Scheduler only
  when the answer actually changed.
- **The API key is the one exception**, and applied immediately. The Save rule is about the
  registry, the Task Scheduler and `settings.json`; a credential is none of those. Deferring it
  would mean holding the secret in a field until the user pressed Save, and a Cancel that silently
  discarded a key they had just pasted would be its own kind of wrong.
- **The window stays open when there is something left to say** — a failure, or a language
  change, which needs a restart before it shows. Otherwise it closes.
- Its state is not reused between sessions: it is constructed on demand, not pre-warmed. It is
  the one window in the product with no latency target.

## Help

The Help tab renders `Help.md`, a Markdown file shipped **beside `FlickGit.exe`**. It is
**read-only, and shown once when the window opens** — there is no Edit button, no Reload button and
no path along the bottom.

There were all three, on the reasoning that shipping the page as a file rather than compiling it in
meant it could be rewritten without a build. That was true of the *file* and wrong about the *tab*:
this is documentation, and a row of controls beneath it invites the user to maintain a page they
came to read. Per Hard Requirement 1 the two buttons, the path label and their four language keys
were deleted rather than hidden.

What that leaves is a loose file with nobody editing it, and the honest consequence is that it could
be embedded like the `.lang` files — which would also remove the only way the page can go missing.
It is not, yet; the install layout and the MSI's file list both name it.

The renderer is ours, some three hundred lines: headings, paragraphs with soft wrap, lists,
quotes, rules, fenced code, and `**bold**` / `*italic*` / `` `code` `` / `[text](url)`. Not a
Markdown library — CLAUDE.md fixes the dependency list at three MIT packages, and a fourth for
one tab rendering one file we ship ourselves is not that trade. Anything the renderer does not
understand shows as its own source text, which for a help page is a legible failure.

A missing or unreadable file is still reported *as the page*, with the path — but the wording
changed with the buttons. It used to say "create it and press **Reload**", which was an invitation;
with nothing to press it is a broken install, and the page says so and names reinstalling.

## About

The icon, the version, one sentence about what the tool is, `by o0Zz`, and a link to
<https://github.com/o0Zz/FlickGit/>. The tray's About entry opens this tab rather than a notice
of its own, so the version, the help page and the repository link live in one place.

## Persistence

```text
%LOCALAPPDATA%\FlickGit\settings.json     schemaVersion + general settings
%LOCALAPPDATA%\FlickGit\actions.json      user actions + built-in overrides
%LOCALAPPDATA%\FlickGit\commit-prompt.md  what the AI is asked for a commit message
%LOCALAPPDATA%\FlickGit\pull-request-prompt.md   ...and for a pull request
%LOCALAPPDATA%\FlickGit\changelog-prompt.md      ...and for a changelog
%LOCALAPPDATA%\FlickGit\icons\            user-supplied .ico files
%LOCALAPPDATA%\FlickGit\Logs\
```

Both JSON files carry `schemaVersion`. An unknown future version is refused with a clear
message rather than silently migrated downward. Writes are atomic: write temp, then
`File.Replace`.

**Almost nothing per-repository lives in `settings.json` any more.** `schemaVersion` 2 dropped
`allowUpstreamCreation`, a dictionary keyed by repository path, and the answer it held is now
`flickgit.allowUpstreamCreation` in the repository's own config — where it cannot go stale when
the repository is moved, and can be seen and reset. Per **Hard Requirement 1** the key was deleted
rather than migrated: every repository asks once more, and then never again. `primaryBranch` stays
here as the global default that `flickgit.primaryBranch` overrides, and the recent list stays because
it is a fact about the *user*, not about any one repository.

`schemaVersion` 3 dropped `aiAllowDiffsToLeaveMachine` and `aiDiffConsentShown`, the AI consent pair.
A named provider with a key stored for it is the consent — see **Privacy and secrets**.

**The three prompt files carry no `schemaVersion` and needed no bump.** They are text, not a format:
there is nothing in one a future build could misread, and the only failure — no prompt left in it —
falls back to the built-in and says so. Nothing was added to `settings.json` for them either, so
`CurrentSchemaVersion` stays 3. See **Prompt**.

**API keys are never written to these files.** Windows Credential Manager or DPAPI only.

## Registry synchronisation

Saving settings regenerates the shell integration:

1. Compute the desired registry state from the Action Catalog
2. Delete only keys under FlickGit-owned paths
3. Write the new keys
4. Verify by reading back; report failures in the UI

Never enumerate or modify registry keys the tool did not create.

---

# Error Handling

Never swallow Git errors. Display the operation, the Git error, the repository path, and a
suggested next action.

```text
Rebase stopped because of conflicts.

Resolve the conflicts, stage the files, then continue with:

git rebase --continue
```

```text
Unable to switch to main because local changes would be overwritten.

No files were modified or discarded.
```

Never show generic errors such as "Something went wrong."

When an operation fails midway, preserve repository state and explain what happened.

---

# Safety Rules

Absolutely avoid destructive Git operations by default. Never automatically execute:

```bash
git reset --hard
git clean -fd
git clean -fdx
git checkout -- .
git restore .
git branch -D
git push --force
```

Any destructive operation requires explicit user intent, expressed in the moment. Never
discard uncommitted work.

The hotkey trigger and the palette are **not** shortcuts around these rules. Actions
marked `RequiresConfirmation`, and every operation in the list above, require a second
explicit confirmation regardless of surface. Force-push is never offered from any of them.

---

# Notifications

Native Windows notifications or small non-intrusive dialogs.

```text
Pull completed successfully.
```

```text
Committed 5 files
8f9ab42 fix: handle rebase conflicts
```

Avoid unnecessary confirmation dialogs. Optimise for one-click workflows.

---

# Logging

Lightweight diagnostics under `%LOCALAPPDATA%\FlickGit\Logs\`, with rotation.

Log: Git command name, duration, exit code, repository path, sanitised errors, and the
latency measurements below.

**Never log:** API keys, credentials, diff contents, file contents, commit message bodies.

---

# Performance Targets

Every one of these must be measurable and surfaced by `flick diag timings`.

| Path                                       | Target | Hard limit |
|--------------------------------------------|--------|------------|
| CLI stub start → exit                      | 30 ms  | 80 ms      |
| Trigger → commit window painted            | 120 ms | 250 ms     |
| Popup file summary populated               | 110 ms | 250 ms     |
| Palette painted after hotkey               | 80 ms  | 150 ms     |
| Commit window visible (service warm)       | 120 ms | 250 ms     |
| Commit window visible (cold fallback)      | 900 ms | 1500 ms    |
| Popup → commit window handoff              | 60 ms  | 150 ms     |
| Status + numstat merge                     | 60 ms  | 150 ms     |
| Click → rendered diff (prefetched)         | 80 ms  | 200 ms     |
| Click → rendered diff (cold)               | 250 ms | 600 ms     |
| Re-diff after edit, 2,000-line file        | 120 ms | 300 ms     |
| AI first token (Haiku 4.5, capped diff)    | 400 ms | 1.5 s      |
| AI complete message                        | 800 ms | 3 s        |
| AI request timeout (silence, not total)    | —      | 8 s        |
| Log window painted, first 200 commits      | 250 ms | 600 ms     |
| Commit selection settled -> file list      | 150 ms | 400 ms     |
| Blame painted, 2,000-line file             | 250 ms | 600 ms     |
| Blame previous revision, one step          | 200 ms | 500 ms     |
| Pull request window painted                | 250 ms | 600 ms     |
| Pull request plan settled -> summary       | 200 ms | 500 ms     |
| AI description first token                 | 600 ms | 2 s        |
| AI changelog first token                   | 600 ms | 2 s        |
| Commit + push, warm, excluding network      | 400 ms | 1 s        |
| `IExplorerCommand::GetState`               | 20 ms  | 50 ms      |
| `IExplorerCommand::GetTitle` (branch read) | 20 ms  | 50 ms      |
| Input hook proc                            | < 1 ms | 5 ms  *(see below)* |
| Resident idle working set                  | 80 MB  | 150 MB     |

The **input hook proc** row is the one exception to "surfaced by `flick diag timings`", and it has
to be: `OperationTimings.Record` enqueues onto a `ConcurrentQueue`, which allocates — and an
allocation inside the proc risks a GC pause inside the very 300 ms budget whose overrun silently
unhooks the feature. Two `static long` counters (last input tick, inputs seen) reported by
`flick diag doctor` are the honest substitute: a user who has been typing for an hour and sees "last
input seen 47 min ago" has just been told the hook is dead.

Explorer integration must never block on network operations. Never perform `git fetch`,
`git pull`, AI requests or large diff parsing while Explorer is building a context menu.

If the median AI first token exceeds 1 s in real use, the diff cap is too high. Check that
before blaming the provider.

---

# Coding Guidelines

Prefer:

- Small focused classes
- Constructor dependency injection, typed, with no `IServiceProvider` anywhere but the
  composition root — see **Hard Requirement 3**
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
- Business logic in WPF code-behind
- Business logic inside any shell extension
- Parsing human-readable Git output
- Abstractions with a single implementation
- Compatibility shims, `[Obsolete]` members and migration code — see **Hard Requirements**

---

# Implementation Phases

## Phase 1 — The commit path works

Repository detection · Git process runner · registry context menu (Commit / Push,
Pull (rebase), Open terminal) · commit window · file list with numstat · stage/unstage ·
**read-only** side-by-side diff viewer · commit · error handling · logging.

Definition of value: the user can right-click, review, and commit, faster than TortoiseGit.

## Phase 2 — Branches, push, live editing

**Branch ComboBox** with switch/create resolution · branch validation · primary branch
resolution and warning · push with upstream handling · the **Branches** action in the submenu ·
**Clone** with clipboard prefill and progress · **submodule update after pull** ·
**editable right pane** · encoding and line-ending preservation · atomic save ·
external-modification detection · staged-vs-worktree restage prompt.

## Phase 3 — Speed

Resident service · Native AOT CLI stub · named pipe IPC with fallback · window pre-warm ·
foreground activation · tray icon and menu · diff prefetch cache · notifications.

Definition of value: every window opens in under 150 ms.

## Phase 4 — The trigger and AI

Global hotkey trigger · `IShellWindows` folder resolution with tab ambiguity · staging defaults ·
queued Enter · AI provider abstraction · Anthropic and OpenAI providers · streaming · warm
connection · diff capping and redaction · API key in Credential Manager · commit & push guardrails.

Definition of value: trigger, Enter, done.

**The quick-commit popup was built and then removed.** It shipped as this phase describes it —
pre-warmed, cursor-anchored, closing on focus loss, with `Details…` handing off to the commit window
— and it was deleted afterwards in favour of the trigger opening the commit window directly. The
argument is in **Commit Window**; what it cost is one surface, `Ctrl+R`, the MRU fallback and the
60 ms handoff budget, and what it bought is one place where a commit behaviour can live. The AI
generation and the queued Enter moved to the commit window rather than dying with the popup, which
is the part worth checking against this list: everything above still exists.

**Still open:** the two Explorer-scoped input hooks (`WH_KEYBOARD_LL` for a key,
`WH_MOUSE_LL` for a side button). **There is no settings value for either**, which is what the
Explorer Trigger section requires: `TriggerKind` is `Hotkey` or `None`, and both do exactly what
they say. A third name that silently registered a global hotkey instead would be a setting nobody
could use. The hotkey is the shipped default and the whole feature works without them.

## Phase 5 — Customisation

Action Catalog · settings window · context-menu customisation · menu icons · registry
synchronisation · repository palette with completion · Ollama provider · speculative
generation · `diag` commands.

**Done:** the `diag` commands, the **repository palette** -- discovery by scan root plus the recent
list, a cached overview refreshed asynchronously, subsequence filtering scored by contiguity and MRU
rank, action mode on a space or `>`, branch completion, the exact command in the footer, and
`Ctrl+Enter` to pull every repository that is behind -- and the **Action Catalog**.

The catalog is the single definition of what FlickGit can do. It replaced three lists: a hard-coded
array in `ShellIntegration`, a second one in the palette, and the verb table -- two of which carried
their own language keys for the same words. The context menu and the palette are both projections of
it now, `actions.json` adds to it, and `flick run <id>` is how a surface that can only issue a command
line reaches an entry that has no verb of its own.

Everything still goes through `Verb` and `VerbRunner`. CLAUDE.md requires the fast surfaces not be
"shortcuts around these rules", and having exactly one route to Git is the only way to make that
structural rather than a promise.

**Phase 5 is complete.** Two of the items it lists were dropped rather than built, and this is the
record of why so they are not proposed again:

- **The full settings app was not built, and will not be.** Its largest section is a drag-and-drop
  reorderable action list with per-row icon pickers and an inline action editor -- more UI than
  Phases 1 to 4 put together, and almost all of it a graphical front end for a file that is already
  documented and hand-editable. `actions.json` is the interface for the menu, and
  `flick diag doctor` reports what it resolved to and names the reason when an entry is refused.

  What did get built, afterwards, is the **small window** in the "Settings" section above: the
  context menu on or off, start with Windows, three commit switches, the language picker, a Help
  tab rendering `Help.md`, and an About tab. Those are the settings whose JSON key nobody can guess
  before they have found the file, which is a different argument from the one that killed the
  action editor -- and the window is one screen with a Save button, not a second interface.
- **The Ollama provider was not built, and then it was.** The original argument was that Anthropic
  and OpenAI cover the feature, which was true of the *feature* and missed the point of the
  provider: the other three all send source code to a third party, so a policy that forbids that
  leaves the user with no AI at all rather than a slower one. **GitHub Copilot** was added first and
  did not change this -- it is a hosted API, and it exists so that a user already paying for Copilot
  needs no second subscription. Ollama answers a different question, and it is now built. Its part
  of **AI Commit Messages** carries the whole of it.
- **Speculative generation is unblocked, and still not built.** This section requires it be
  "automatically disabled when the provider is not local", and with no local provider it could never
  legally enable itself -- so it was dead code by its own safety rule. Ollama removes that
  objection: with `aiOllamaUrl` on loopback the diff never leaves the machine, so the rule permits
  it. It is still not built, because nothing about the argument *for* it has been re-made: it wants
  a foreground-window watch, a 1.5 s dwell, a diff-hash cache and a setting, to save a wait that
  a queued Enter already hides. It is a candidate now rather than an impossibility, which is the
  only thing that changed.

The context menu is still customisable, just not by clicking: `builtIns` in `actions.json` hides,
relabels and reorders any built-in, and user actions add to every surface at once.

Second-token completion comes from `ActionParameter`, which declares `Branch` and `Tag`. The remotes
and stashes kinds are still absent, because nothing takes one.

`Tag` is the odd one and it is worth saying why: it is the only parameter kind with **no completion
source**. The second token after `tag` is a tag being *created*, so the repository's existing tags are
the one set of values it will never be — completing from them would steer the user towards the only
answer Git is certain to refuse. The palette validates what was typed instead and shows the
consequence inline, which is what the branch ComboBox already does for a new branch name.

Making that work exposed a latent bug worth recording: the parameter was being **dropped**.
`PaletteViewModel` collected it, built a row from it, and then raised `ActionRequested` without it, so
choosing a branch in the palette opened the Switch picker with the branch thrown away. `ActionRunner`
now takes an argument and passes it into the `Verb` a `WindowRun` builds, so both kinds reach the verb
that was always ready to receive one.

## Phase 6 — Deep shell integration

Sparse MSIX package · `IExplorerCommand` · repository-aware visibility via `GetState` · the branch
name in the Commit label · dynamic submenu from the Action Catalog · line and hunk staging.

**Line and hunk staging is done.** `Core/Diff/Hunks.cs` groups the in-memory diff into hunks and
turns any set of rows into a unified patch; `PatchService` applies it with `git apply --cached`, which
touches the index and never the working tree. The same function serves whole-hunk and selected-line
staging, and the same patch applied in reverse serves unstaging — so the two cannot disagree about
what a hunk is.

Three things that were not obvious until it ran against real Git:

- **The line endings are the whole difficulty**, as this section always said. `git apply` compares a
  context line to the index byte for byte, and in a CRLF file the line's content *includes* its
  carriage return. Every emitted line is re-terminated from the `FileText` it came from, per line for
  a mixed-ending file.
- **A file ending with a newline produces one diff row past its last line** — the empty string after
  the final terminator. Emitted as context it made the hunk header claim one line more than the file
  has, and Git refused every patch whose context reached the bottom of a file. Hand-built test rows
  did not have that row, so the unit tests all passed while the feature was broken.
- **Unstaged rows are demoted, not dropped, and the two directions are not symmetric.** An unstaged
  deletion becomes context; an unstaged insertion vanishes. Reversed, the patch applies cleanly and
  stages the opposite of what the user picked.

**This needed a third staging state.** `CommitFlow` runs `git add` over every ticked path, which would
swallow the hunks the user left out, and `git restore --staged` over every unticked one, which would
discard the hunks they kept. `GitFileChange.HasChosenHunks` means "the index already holds exactly what
was picked" and puts the file in neither list. It is set by the viewer, carried across a refresh, and
dropped as soon as the index no longer holds anything of the file — so it is spent by a commit without
anybody having to clear it.

**`IExplorerCommand` is done, and it did not need MSIX.** That was the mistake in the plan above:
the sparse package is required for the Windows 11 *primary* menu, not for a dynamic handler.
`ExplorerCommandHandler` on a verb key is honoured in the classic menu with an ordinary per-user
COM registration, so `GetTitle` and `GetState` were reachable all along without package identity or
a signature.

**The DLL ships unsigned, and that is not a shortcut.** Windows does not check Authenticode when it
loads an in-process COM server, and the registration is `HKCU` only — so there is nothing to sign
*for*. TortoiseGit signs its own shell DLL because it ships a public installer, not because the
shell requires one. Three things are worth knowing before assuming that holds everywhere:

- **Smart App Control** refuses unsigned binaries when it is on. It only enables itself on a clean
  Windows 11 install, but on such a machine the DLL will not load and there is **no FlickGit menu at
  all**. This used to claim the entries fell back to the static verbs, and they never did: the layout
  was chosen by the file being *present*, not by Explorer managing to *load* it — which cannot be
  known from outside `explorer.exe` — so such a machine got the handler registered and no verbs
  either way. A fallback that could not fire is why the verbs were deleted rather than kept.
- **AV and EDR** see an unsigned DLL loading into `explorer.exe`, which is a textbook malware shape.
  Same exposure this document already notes for the input hook, and most likely to matter to someone
  who downloaded a release rather than built one.
- **WDAC policy** can require signing machine-wide.

Signing is therefore a distribution concern, not a functional one — and the pipeline for it is
built and inert: `build.yml` submits tag builds to the **SignPath Foundation**, whose free tier
covers open-source projects, and skips every signing step when `SIGNPATH_API_TOKEN` is empty. See
README's *Signing* section for the four settings that turn it on.

**Azure Trusted Signing was the wrong recommendation for this project**, and the reason is worth
recording so it is not proposed again: it is limited to organizations in the USA or Canada with
three or more years of verifiable history. SignPath issues an OV certificate from Sectigo, needs no
hardware token, and costs nothing for a public repository — the trade being that the publisher shown
to users is "SignPath Foundation" rather than the project, because verification is against the
repository rather than a person. That is also why there is a `LICENSE` file: an OSI-approved licence
is the eligibility requirement, and the README had claimed MIT for some time without one being
present.

**Repackaging as an `.msi` does not help *with this*, and that half stands.** Chrome's download
protection keys on publisher reputation and treats installers as a *more* dangerous file type than
archives, so an unsigned MSI from a new publisher is warned about at least as loudly as a zip — with
SmartScreen's unknown-publisher wall added when it runs. The container is not what is being objected
to.

There is now an MSI anyway, and **for the other problem this section names three paragraphs up**:
"the DLL stays locked while Explorer runs: replacing the binary needs an Explorer restart". A zip
cannot do that in the right order and an installer can. See **Installer** for the sequence. It
carries the signed binaries and is not itself signed, so nothing above changes: whichever asset is
downloaded is warned about until signing is live.

**The sparse MSIX package remains the only part that cannot work without a certificate at all.**

`FlickGit.Shell.dll` is a Native AOT COM server on the two root verbs. It does two things:

- **`GetTitle` puts the branch in the label** — `Commit / Push (feature/storage-gw)…`, which is what
  this was built for. Before the ellipsis, not after: the ellipsis means "this opens something" and
  belongs at the end of the whole label.
- **`GetState` hides both root entries outside a repository**, which is the repository-aware
  visibility this phase always wanted.

Five things that were not obvious until it ran:

- **The vtable slot numbers are the whole risk.** `IShellItemArray::GetItemAt` is slot 8, not 9 —
  slot 9 is `EnumItems`, whose only argument is a pointer. Calling it with a `DWORD` in that register
  and reading the result back as an `IShellItem` is an access violation, and in the real
  configuration that access violation is inside `explorer.exe`. Every slot in the DLL is now
  commented with the count that produced it.
- **Test it out of process first.** A throwaway CLSID pointing at the published DLL, driven from a
  console app with a real `IShellItemArray`, found that bug in a process nobody minds losing. The
  same bug found by registering the handler first would have taken the desktop down.
- **Native AOT, not `comhost`.** The alternative loads the CLR into `explorer.exe` on the first
  right-click. CLAUDE.md's own argument for `flick.exe` being AOT applies with more force to a DLL in
  somebody else's process.
- **`DllCanUnloadNow` must return `S_FALSE` forever.** The .NET runtime cannot be unloaded and
  reinitialised in a live process, so agreeing to unload is agreeing to a crash. The cost is that the
  DLL stays locked while Explorer runs: replacing the binary needs an Explorer restart, though an
  uninstall takes effect immediately because it only removes registry keys.
- **The handler is registered only when the DLL is actually beside `flick.exe`.** Native AOT runs on
  publish, so a `dotnet build` working tree has no native DLL, and `Install` refuses there rather
  than writing a CLSID with nothing behind it. It used to fall back to static verbs — a working menu
  in the wrong block — and that fallback is deleted; see **The context menu is a handler**.
- **`IExplorerCommand` was the wrong interface, and the reason is placement.** It shipped, it worked,
  and it was replaced by `IContextMenu` — because a verb-hosted handler inherits the verb's position,
  and no verb can reach the block Explorer reserves for shell extensions. See **The context menu is
  a handler**. The per-verb CLSIDs live on only in `ShellCommandIds.RetiredClsids`, so an uninstall
  can still remove what an earlier version wrote.

**Still open:** the sparse MSIX package, and only that. It is the one remaining way to reach the
Windows 11 primary menu rather than "Show more options", and it needs package identity and a
code-signing certificate; without one there is nothing to install. The global hotkey is the fast path
regardless.

## Phase 7 — Reading history

The log window: a commit list, a message, a file list and the read-only diff — and the reason it
exists, **the combined diff over a multi-selection**. Its own section above carries the rules.

Then **blame**, with the walk back through `previous` that is the reason to have it — and with it
the first FlickGit entry on a *file* rather than a folder, which is what `ActionSurfaces.File` and
the handler's `*` registration exist for.

Definition of value: the user can answer "what changed between these commits" and "who wrote this
line, and what was here before" without leaving FlickGit, and can hand the first answer to somebody
else — as a `.patch` for somebody who will apply it, or as a **changelog** for somebody who will
never read the code. The changelog arrived after the rest of the phase and needed no new plumbing:
one more `AiContextBuilder` method, one more prompt file, and `CommitRange.Commits` so the window
that writes it is describing exactly the range the diff and the patch are of.

## Phase 8 — The repository's own settings

The **Repository** window: the identity it commits as, its remotes, and the two preferences FlickGit
keeps per repository. Its own section above carries the rules.

Definition of value: "which identity does this commit as" and "where does this push to" are
answerable, and changeable, without a terminal.

Two things came with it. `flickgit.primaryBranch` and `flickgit.allowUpstreamCreation` live in the
repository's own config, which is what removed the last path-keyed dictionary from `settings.json`
(**Persistence**), and `RemoteService` is the first code in the product that writes a remote at all
— `PushService` and `TagService` still only ever read the *names*, which is all either of them
needs.

This is the first feature that is not on the commit path at all, and the thing that makes it
belong in a tool that is "not a complete Git client" is that it *performs nothing*. Reading
history is the one everyday operation that changes no state and had no surface. The list of what
the window refuses to do is in its section, and it is the boundary.

---

## Phase 9 — Proposing the branch

The **Pull request** window: GitHub, GitLab and Azure DevOps, cloud and self-hosted, with the
description written by the same AI that writes commit messages. Its own section above carries the
rules.

Definition of value: the branch you have just pushed becomes a pull request without opening a
browser, finding the repository, remembering which of three interfaces this one is, and typing a
title that repeats what the commits already say.

**This is the first feature that talks to something other than Git and the AI provider**, and that
is what most of its design is about. Three things came with it, each of which changed code that was
already there rather than sitting beside it:

- **`ICommitMessageGenerator` became `IAiGenerator`**, taking an `AiPrompt` rather than a
  `CommitContext`. The streaming, the eight-second silence timeout, the redaction and the
  consecutive-failure counter are the expensive parts of the AI feature, and they were reachable
  only from the commit surface. `CommitMessageService` became `AiTextService` for the same reason.
- **`ApiKeyStore` became `CredentialStore`**, keyed by a target string rather than by an AI
  provider. A forge token is a secret filed under a different name and nothing else, and the
  alternative was a second copy of four P/Invokes.
- **`HistoryService.GetFilesAsync` takes two revision specs** rather than a `CommitRange`. The log
  window builds one out of a selection; a pull request has a merge base and a HEAD, and synthesising
  a fake `CommitRange` to satisfy a signature would have been the wrong kind of reuse.

**Still open, and deliberately:** no reviewers, no labels, no work items, no merging or approving,
no cross-fork requests. Every one of those is argued through in the section's own list.

The two per-repository keys it adds — `flickgit.pullRequestTarget` and `flickgit.forge` — have no
row in the **Repository** window yet. Both are reached today through a refusal that names the
`git config` line, which is honest but is the one loose end worth closing: that window is already
the surface for "this repository's own defaults", and it now knows about two it does not show.

---

# Testing

The scope is fixed by **Hard Requirement 4**: the core, the commit sequence, the safety rules, the
working tree and the command-line grammar. Nothing else. What follows is the list of behaviours
those five bullets actually mean, and it is the whole list.

`tests/FlickGit.Core.Tests` is the only test project. It targets `net9.0`, references
`FlickGit.Core` and nothing else, and fakes `IGitProcessRunner` rather than starting `git.exe` — so
the *arguments* are assertable, which is the half a temporary repository would hide.

## Parsers

- `--porcelain=v2 -z`: ordinary changes, renames, untracked, conflicted, the branch header
- `--numstat -z`: a rename, a binary file reporting `-`, and a path containing a literal `=>`
- `--name-status -z`: the ordinary letters, and a rename whose score is glued to the letter and
  which consumes two extra fields
- `config --local --list -z`: the key ending at the *first* newline so a value keeps its own, a key
  set with no value at all, a remote whose capitals survive and whose name contains a dot, a
  `remote.*.fetch` refspec that is not a URL, and `--unset` exiting 5 for a key that was never there
- `blame --porcelain`: metadata reused across a commit's later lines, `previous` and `boundary`,
  the forty-zero sha, a content line found by its tab rather than by known keys, and the author's
  own timezone
- the `git log` format: every field of one record, a message containing the field separator and
  newlines, the root commit's empty `%P`, and a merge's parents
- `CommitRange.Resolve`: newest-first ordering, a gapped selection and its implicit count, the
  root commit's empty-tree base, and a merge's first parent
- `ForgeUrl.TryParse`: every remote spelling for all three services — GitHub Enterprise's
  `/api/v3/`, a nested GitLab subgroup encoded whole, Azure DevOps' four shapes and the collection
  URL each implies, an unrecognised host resolving to nothing rather than to a guess, and
  `flickgit.forge` overriding a hostname that actively misleads
- `PullRequestPrompt.Split`: the title off the first line, with and without the blank line, and a
  heading marker, bold, or a code fence the model was asked not to write
- Paths containing spaces and non-ASCII characters

## The commit sequence

`CommitFlow`, which owns the order:

- Unticked-but-staged files are taken out of the index before the commit
- A file whose deletion is already staged is not passed to `git add`, where the pathspec would match
  nothing and fail the command, while one deleted from the working tree only still is
- Naming the current branch performs no switch at all
- An existing branch switches, refreshes, and aborts when a selected file changed as a result
- An invalid ref name is rejected before any Git command runs
- A new branch is created, committed to, and pushed with `-u`
- A blocked switch stops the flow with nothing committed

`PullRequestFlow`, which owns the other one:

- The branch is pushed **before** the request is created, and the create can see that it was
- A diverged branch, and a branch behind its own upstream, are refused with nothing pushed and
  nothing created
- Declining to create an upstream stops the flow rather than falling through to the create — the
  bug this test was written for
- An already-open request is reported instead of a second being created
- A refused credential is asked for once more, with `forcePrompt`; any other failure is not retried
- An empty title is refused before any Git command or request runs

## The safety rules

- Untracked files are not in the default staging set, including when they are the only changes
- Secret-matching files are excluded even when tracked and modified
- A blocked `git switch` leaves the working tree and index untouched
- Stash-switch-restore restores only the stash it created, and reports a conflicting restore
- `branch -d` is what an ordinary delete runs; an unmerged refusal is reported rather than escalated,
  and `-D` appears only when force was explicitly asked for
- Deleting the current branch runs no Git command at all, force or not
- A remote branch deletion pushes a fully qualified `refs/heads/…` ref, with no force and no lease,
  and a name whose prefix is not a configured remote resolves to nothing rather than being guessed
- A diverged push is refused, with no state change
- Reverting a file names one path after `--` and takes both sides from HEAD — one path per call
  however many rows were selected, because the caller loops — and a row HEAD does not have —
  untracked, added, renamed, conflicted — never reaches a command at all
- The file menu's `git rm` names one path after `--` and carries no `-f` and no `-r`, and both it and
  `git add` pass the path as `:(literal)…` so a bracketed file name cannot glob onto another file
- An untracked path answers "not tracked" from one `ls-files -z` read, and no `rm` runs at all
- No argument list ever contains `add -A`, `add .`, `reset --hard`, `clean -fd` or `push --force`
- Opening a pull request reaches a remote only through `PushService`, so no `--force`, `-f` or
  `--force-with-lease` can appear on that path either
- Every read carries `--no-optional-locks`

## The working tree

The one place where thoroughness is the point rather than the cost:

- Round-trip save preserves UTF-8 with BOM, UTF-8 without, UTF-16LE, CRLF, LF, mixed endings, and
  the absence of a trailing newline
- A change block pairs each deletion with the insertion that replaced it, not with whichever
  insertion happens to sit at the same offset — with the word-level spans computed against the line
  actually paired, and both panes still holding exactly one entry per row
- Reverting lines reconstructs the file: a modified line goes back, an insertion is dropped, a
  deletion is restored, an unselected change survives, a trailing newline is neither gained nor
  lost, and reverting every row reproduces the left side exactly
- Save is refused when the file changed on disk after load
- The document the editor holds converts back to file text without its alignment fillers, and
  without gaining or losing a trailing newline
- A binary file is never opened as text

## What may leave the machine

The AI's payload builder, which is the only part of the product that can send a user's credentials
to a third party:

- Lock files, generated code and minified assets are never included
- Secret-matching paths are never included, by flag *and* by pattern
- A credential inside an ordinary source file is redacted
- A diff under the cap is sent verbatim; above it, each file keeps forty lines and a `[truncated]`
  marker, and the token ceiling is applied *after* the per-file cap
- The request names only the included paths, carries `--no-optional-locks`, and asks for `HEAD`
  rather than `--cached`
- Nothing is asked of Git at all when every file is excluded
- A rejected key fails rather than returning a partial message

## The provider streams

Four wire formats — three SSE and one not — all read a few bytes at a time so a reader that only
works on a whole response does not pass:

- Anthropic `content_block_delta` / `text_delta` concatenated, every other frame ignored
- OpenAI `response.output_text.delta` concatenated, `[DONE]` not handed to a parser
- Copilot `choices[0].delta.content` concatenated, and the two frames that carry no text —
  a delta that is only a role, and an empty `choices` — ignored rather than treated as a fault
- Ollama's newline-delimited JSON concatenated, with no `data:` prefix and no blank-line separator,
  and the `done: true` line — empty content plus timing statistics — neither appended nor treated as
  a fault
- Ollama's error, which is a bare string where the other three wrap it in an object, fails the
  request rather than returning a silently empty message
- Ollama with no model set is refused before a request is made at all, and the refusal names
  `ollama list`
- The stored GitHub token is exchanged and **never** sent to the completion endpoint, which the
  other two keyed providers cannot get wrong because their key *is* the header
- The Ollama request carries **no** `Authorization` header at all, which is the local provider's
  equivalent assertion: there is nobody to authenticate to, so a credential there would mean one had
  been wired in where none exists
- The request body carries `max_tokens: 150` (`num_predict` for Ollama), `stream: true` and no
  `thinking` field
- A fenced message is unwrapped

## The command-line grammar

- `<path>` defaults to the working directory it was given, not to the process's own
- The path-less verbs stay path-less
- Explorer's quoted `%V` for a drive root is unquoted
- An unknown verb becomes help plus a reason
- Every verb the help text advertises parses
- `ai` and `autostart` carry their sub-tokens as the two positional slots

## Verified by running it, not by a test

The resident service, the named pipe, the tray icon, the registry writer, autostart, notifications,
window reuse, and every window — the log and blame windows included: its list, its multi-selection, its gap
disclosure and the patch it saves are checked by opening it and by running `git apply --check` on
the result. Checked by hand when the feature is built — start the service, run
the verb, read `flick diag timings`, confirm the numbers against **Performance Targets** — and
recorded in the phase's notes. A test that has to construct a `Window` is testing WPF.


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
- Important logic has tests
