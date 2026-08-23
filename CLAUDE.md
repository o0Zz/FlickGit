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

Two rules that override convenience, habit, and anything elsewhere in this document that reads
like a suggestion.

## 1. Break things freely

There is no compatibility obligation to anything FlickGit has already shipped. When a better
design appears, take it and change whatever it touches:

- **Settings, `actions.json`, cached state** — change the shape, drop keys, rename them. Bump
  `schemaVersion` and let an old file be refused. Do not write a migration.
- **Registry layout, CLI verbs, exit codes, action ids, IPC messages** — rename or remove them
  outright. No aliases, no shims, no deprecated spelling that still happens to work.
- **Types in `FlickGit.Core`** — there is no external consumer. Change the signature rather than
  adding an overload beside it.
- **Delete rather than deprecate.** No `[Obsolete]`, no "legacy" path kept alive, no second code
  branch preserved for the way it used to work. When something is replaced, the old thing goes in
  the same change.

This is a per-user tool with one install, no plugin API and no other consumers. A migration path
costs real complexity to serve a user who does not exist.

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

- **Parsers.** `--porcelain=v2 -z` and `--numstat -z`, the two places where a wrong byte becomes a
  wrong file list.
- **The commit sequence.** `CommitFlow` — stage, switch, verify, commit, push, in that order.
- **The safety rules.** A blocked switch changes nothing; a stash restores only the one it created;
  a diverged push is refused; `add -A` never appears in an argument list; untracked and
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
│   │                        ResidentWindow is the pre-warm and the show sequence,
│   │                        which all three windows do identically.
│   ├── Trigger/             The global hotkey and Explorer folder resolution.
│   ├── Ai/                  CommitMessageService: consent, the failure counter and
│   │                        the streaming state machine. Here rather than in Core
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
│   ├── Status/              porcelain v2 and numstat parsing, StatusService,
│   │                        StatusComparer
│   ├── Diff/                DiffService, FileTextLoader, WorkingTreeWriter,
│   │                        DiffDocument, and Hunks + PatchService -- the patch
│   │                        generator and `git apply --cached`
│   ├── Commits/             CommitService, and CommitFlow -- the stage/switch/
│   │                        verify/commit/push sequence
│   ├── Actions/             ActionCatalog and its data: GitAction, ActionRun,
│   │                        ActionSafety, ActionPlaceholders, actions.json
│   ├── Palette/             RepositoryScanner and the cached overview the palette
│   │                        paints from before Git is asked anything
│   ├── Ai/                  What may leave the machine (DiffPayload,
│   │                        CommitContextBuilder) and the two providers. AiEndpoint
│   │                        is the request both of them make.
│   ├── Branches/            BranchService, SwitchService
│   ├── Remotes/             PushService
│   └── Pulls/ Clone/ Secrets/ Matching/ Logging/ Diagnostics/ Models/
│
└── FlickGit.Shell/          Native AOT COM DLL, loaded into explorer.exe. Draws the
    │                        two root menu entries: IExplorerCommand::GetTitle puts the
    │                        branch in the Commit label, GetState hides both outside a
    │                        repository. Hand-rolled vtables, no [GeneratedComInterface]
    │                        -- see Com.cs for why. No ProjectReference, ever.
    ├── Exports.cs           DllGetClassObject, DllCanUnloadNow, IClassFactory.
    ├── ExplorerCommand.cs   The one COM object: IExplorerCommand + IObjectWithSite.
    ├── FolderResolver.cs    IShellItemArray for a clicked folder; the site chain for
    │                        a clicked background.
    ├── GitHead.cs           The branch, from .git/HEAD. No git.exe, no pipe.
    └── RepositoryLookup.cs  One answer per right-click instead of four.

    This does NOT reach the Windows 11 primary menu -- that still needs a sparse MSIX
    package and package identity, which is the part of Phase 6 still open. An
    ExplorerCommandHandler on a verb key is honoured in the classic menu with an
    ordinary per-user COM registration, which is what this uses.

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
flick pull-rebase <path>             + submodule update when applicable
flick pull-rebase-autostash <path>
flick push <path>
flick switch <path> [branch]         branch picker when omitted
flick tag <path> [name]              tag window when omitted; creates it when named
flick status <path>
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
│ [ Commit & Push ]   [ Commit ]                              [ Cancel ]     │
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
esc        cancel a queued commit or a running generation; otherwise close
```

**Enter commits rather than inserting a newline**, which is the one thing about this window nobody
can guess — so the footer says so whenever there is no outcome to report in its place.

**Except while the diff pane has keyboard focus.** Its right-hand pane is an editor over the user's
working tree, where Enter is a newline in their file. Committing instead would be surprising and
unrecoverable in the same keystroke, so the whole map above is suspended there.

**Esc closes, always — including from inside the diff pane, and including while the AI is still
writing.** One key, one outcome. It briefly cancelled a running generation instead and left the
window open, needing a second press; since generation starts on every open, that made Esc look
broken for the first half-second of the window's life. Closing cancels the generation on the way
out, and a queued Enter cannot fire into a window that is gone.

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

Left-hand base by comparison mode:

```bash
git -C <repo> show HEAD:<path>    # Working tree ↔ HEAD
git -C <repo> show :<path>        # Working tree ↔ Index
```

For an untracked file, the left side is empty.

Use **DiffPlex** for the line diff, with a word-level pass inside changed line pairs for
character diffs. Do not write a Myers implementation.

## Editor component

**AvalonEdit**, two instances, left read-only and right editable.

- Synchronised scrolling locked to the diff alignment, not to raw line numbers
- Change bars and line backgrounds via `IBackgroundRenderer` — never insert a visual element
  per line
- The connector strip between panes drawn as a single visual
- Monospace, DPI-aware; tab width from the file's `.editorconfig` when present
- Syntax highlighting via AvalonEdit `.xshd` definitions

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
in the right pane and press `Revert lines`, and the left side's version of those lines replaces
them.** Landing the caret anywhere inside a hunk without selecting takes the whole hunk, the same
rule hunk staging already uses.

One rule makes this safe enough to offer on a single click, for an operation that otherwise reads as
"discard my work":

> **It is an edit, not a Git operation.** The reverted text goes into the editor exactly as if it had
> been typed there. Nothing is staged, no process runs, and nothing reaches the disk — so `Ctrl+Z`
> takes it back, and `Ctrl+S` is still the only thing that writes. **Safety Rules** forbids
> discarding uncommitted work; until the user saves, none has been discarded.

That is also why there is no confirmation dialog. A confirmation would be friction protecting
against something that has not happened yet, and the thing it would guard — the save — already has
its own explicit keystroke.

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

If the user is viewing the **staged** diff (`git show :<path>` on the left) and edits the
right pane, they are editing the **working tree**, not the index. The edit does not appear in
the diff they are looking at, and if the file is already staged, the change will not be in
the commit.

This is a well-known source of confusion in TortoiseGit. Handle it explicitly:

- Label the comparison mode permanently in the viewer header: `Working tree ↔ Index` or
  `Working tree ↔ HEAD`
- When the user edits a file that is already staged, show an inline strip: *"This file is
  staged. Your edit is not in the commit yet — restage?"* with a one-click restage
- On commit, restage every edited file if the user chose restage, and warn otherwise. Never
  silently commit a stale staged version of a file the user just edited.

## Guards

- Read-only for binary files, files above the size limit, and files in an unresolved conflict
- Never edit files outside the resolved repository root
- Refuse to save into a path that has become a symlink or junction since load

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

1. User setting
2. Remote HEAD — `git symbolic-ref refs/remotes/origin/HEAD`
3. `main`
4. `master`

Cache the result per repository. Resolving this must never block the menu or the popup: if
resolution has not completed, show the popup without the warning strip rather than waiting.

Switching to the primary branch follows the ordinary switch rules — check
`git status --porcelain`, never discard anything, stop and explain if the switch is refused.

---

# Pull --rebase

```bash
git pull --rebase
git pull --rebase --autostash
git submodule update --init --recursive    # only when .gitmodules exists
```

Show progress in a lightweight dialog, with the submodule update as a distinct step (see
**Submodules**). On conflict, show a clear message and offer to open
the repository status window. Do not automatically abort a rebase.

Use Git's native `--autostash` whenever available — it is safer than a manual
stash/pull/pop. If unavailable, the manual fallback must:

1. Detect whether local changes exist
2. Create a uniquely identifiable stash
3. Run `pull --rebase`
4. Restore **only** the stash it created
5. Never blindly pop an unrelated existing stash

---

# Push

```bash
git push
git push -u origin HEAD    # when the branch has no upstream
```

Ask before creating an upstream. Remember the answer per repository.

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

**Ollama / local:** supported, not the default. Useful when policy forbids sending code off
the machine.

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

Implementations: `AnthropicCommitMessageGenerator`, `OpenAICommitMessageGenerator`,
`OllamaCommitMessageGenerator`, `DisabledCommitMessageGenerator`.

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

Before any diff leaves the machine, tell the user clearly that source code may be
transmitted. Setting off by default, shown once with a clear explanation on first use, not
buried.

Run the secret detector before sending **and** before committing. Patterns: AWS keys, GitHub
tokens, generic API keys, private key blocks, connection strings, passwords. Never send
`.env`, credentials or private keys. On detection, warn and redact.

## Failure behaviour

The AI is an accelerator, never a dependency.

- Unreachable, invalid key, rate limited, or timed out (**hard timeout 8 s**): the message
  field becomes an ordinary editable box with a one-line notice. Commit and push stay fully
  available.
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
    ExplorerCommandHandler = "{F1C7A6D2-3B84-4E5A-9C61-7D2E8A4B5C10}"
    \command  (default)    = "<install>\flick.exe" commit "%V"

HKCU\Software\Classes\Directory\shell\FlickGit.zz.menu
    MUIVerb                = "FlickGit"
    Icon                   = "<install>\FlickGit.exe,0"
    ExtendedSubCommandsKey = "FlickGit.Menu"

HKCU\Software\Classes\FlickGit.Menu\shell\110switch
    MUIVerb                = "Switch branch..."
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
- **`MUIVerb` is a static string**, written once and rendered for every folder on the machine.
  Nothing of ours runs while Explorer builds the menu, so a registry verb cannot know the branch,
  the repository, or anything else about what was clicked. The two root entries get around this
  with an `ExplorerCommandHandler` — see below — and everything in the submenu does not.
- Ship `.ico` files with 16, 20, 24, 32 and 48 px frames; Explorer picks by DPI
- Classic menu icons are **not** theme-aware. Use mid-tone outline glyphs that read on light
  and dark rather than shipping two sets.
- Support a flat-at-root layout as a setting, not only the submenu

**On Windows 11 this appears only under "Show more options" (Shift+F10).** Accept this for
Phase 1; the global hotkey is the real fast path anyway.

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

## Deliberately out of scope

Committing inside submodules, changing submodule pointers, `--remote` updates, and adding or
removing submodules. These are rare, dangerous, and belong in a full client. If the status
shows a modified submodule pointer, display it as a normal changed entry and let the user
decide.

---

# Switch Branch

Lives under **More** in the context menu, and as `switch` in the palette and CLI. It is
separate from the commit-surface ComboBox, which only switches as part of committing.

```text
┌─ Switch branch — d360-portal ───────────────┐
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
      ├── Switch branch…
      ├── Tags…
      ├── Push
      ├── Clone…
      ├── Fetch (prune)
      ├── Repository status…
      └── Open terminal here
```

Two entries in the context menu itself, because those are the two the user performs all day.
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
│  COMMIT MESSAGES (AI)                                    │
│  Written by                                              │
│  [ Anthropic — Claude Haiku 4.5                     ▾ ]  │
│  [ Set API key… ]  [ Remove key ]                        │
│  A key is stored for Anthropic, in Windows Credential    │
│  Manager.                                                │
│  ☐ Allow the diff to be sent to this provider            │
│     Your code leaves this machine only while this is on. │
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
│                              [ Save ]      [ Cancel ]    │
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

So: the provider, a button that opens the existing key prompt, and the consent switch. Three
controls, no model picker and no max-diff field — those are `aiModel` and `aiMaxDiffBytes` in
`settings.json`, guessable once the provider is on, and neither is a thing anyone changes twice.

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

The Help tab renders `Help.md`, a Markdown file shipped **beside `FlickGit.exe`** rather than
embedded. That is the whole point of it: the file can be opened in any editor, changed, and
reloaded from the tab without a build. **Edit Help.md** opens it in whatever handles `.md`;
**Reload** re-reads it.

The renderer is ours, some three hundred lines: headings, paragraphs with soft wrap, lists,
quotes, rules, fenced code, and `**bold**` / `*italic*` / `` `code` `` / `[text](url)`. Not a
Markdown library — CLAUDE.md fixes the dependency list at three MIT packages, and a fourth for
one tab rendering one file we ship ourselves is not that trade. Anything the renderer does not
understand shows as its own source text, which for a help page is a legible failure.

A missing or unreadable file is reported *as the page*, with the path, because "where would I
put one?" is the only question that follows.

## About

The icon, the version, one sentence about what the tool is, `by o0Zz`, and a link to
<https://github.com/o0Zz/FlickGit/>. The tray's About entry opens this tab rather than a notice
of its own, so the version, the help page and the repository link live in one place.

## Persistence

```text
%LOCALAPPDATA%\FlickGit\settings.json     schemaVersion + general settings
%LOCALAPPDATA%\FlickGit\actions.json      user actions + built-in overrides
%LOCALAPPDATA%\FlickGit\icons\            user-supplied .ico files
%LOCALAPPDATA%\FlickGit\Logs\
```

Both JSON files carry `schemaVersion`. An unknown future version is refused with a clear
message rather than silently migrated downward. Writes are atomic: write temp, then
`File.Replace`.

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
| AI request timeout                         | —      | 8 s        |
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
resolution and warning · push with upstream handling · **Switch branch** action under More ·
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
`WH_MOUSE_LL` for a side button). They are selectable in settings.json and currently fall back to
the global hotkey with a logged warning and a `diag doctor` line that says so. The hotkey is the
shipped default and the whole feature works without them.

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
- **The Ollama provider is not being built.** Anthropic and OpenAI cover the feature.
- **Speculative generation is therefore not being built either.** This section requires it be
  "automatically disabled when the provider is not local", and Ollama was the only local provider --
  so with Ollama gone the feature could never legally enable itself. Implementing it would be dead
  code by its own safety rule. If a local provider is ever added, this comes back with it.

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
  publish, so a `dotnet build` working tree has no native DLL — and a verb naming a CLSID with no
  server behind it is a verb Explorer *drops*, turning two working entries into none. The static
  `MUIVerb` and `command` are still written either way, so the handler is an enhancement to a verb
  that works without it rather than a replacement for one.

**Still open:** the sparse MSIX package, and only that. It is the one remaining way to reach the
Windows 11 primary menu rather than "Show more options", and it needs package identity and a
code-signing certificate; without one there is nothing to install. The global hotkey is the fast path
regardless.

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
- Paths containing spaces and non-ASCII characters

## The commit sequence

`CommitFlow`, which owns the order:

- Unticked-but-staged files are taken out of the index before the commit
- Naming the current branch performs no switch at all
- An existing branch switches, refreshes, and aborts when a selected file changed as a result
- An invalid ref name is rejected before any Git command runs
- A new branch is created, committed to, and pushed with `-u`
- A blocked switch stops the flow with nothing committed

## The safety rules

- Untracked files are not in the default staging set, including when they are the only changes
- Secret-matching files are excluded even when tracked and modified
- A blocked `git switch` leaves the working tree and index untouched
- Stash-switch-restore restores only the stash it created, and reports a conflicting restore
- A diverged push is refused, with no state change
- No argument list ever contains `add -A`, `add .`, `reset --hard`, `clean -fd` or `push --force`
- Every read carries `--no-optional-locks`

## The working tree

The one place where thoroughness is the point rather than the cost:

- Round-trip save preserves UTF-8 with BOM, UTF-8 without, UTF-16LE, CRLF, LF, mixed endings, and
  the absence of a trailing newline
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

Two wire formats, both read a few bytes at a time so a reader that only works on a whole response
does not pass:

- Anthropic `content_block_delta` / `text_delta` concatenated, every other frame ignored
- OpenAI `response.output_text.delta` concatenated, `[DONE]` not handed to a parser
- The request body carries `max_tokens: 150`, `stream: true` and no `thinking` field
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
window reuse, and every window. Checked by hand when the feature is built — start the service, run
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
