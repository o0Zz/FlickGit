# ⚡ FlickGit

**Fast Git actions from Windows Explorer.** Right-click, review, commit — without opening a
full Git client.

FlickGit is for the developer who works across five to ten repositories a day and switches
between them constantly. The bottleneck it removes is *not* the commit dialog — it is the cost
of getting to the right repository and back out again.

> Status: **Phase 5, in progress**. Press `Ctrl+Alt+R` for the repository palette. Press `Ctrl+Alt+G` anywhere and a popup opens on the repository Explorer
> is showing, with an AI-written commit message streaming into it. Enter commits and pushes.
> See [Roadmap](#roadmap) for what is built and what is not.

---

## What works today

- **Explorer context menu** — Commit / Push…, Pull (rebase), and under *More*: Switch
  branch…, Push, Clone…, Repository status, Open terminal here. Per-user install, no
  administrator rights.
- **Commit window** — TortoiseGit-style file list with status letters and `+added / -removed`
  line counts, tick boxes that decide the commit, and a side-by-side diff beside it.
- **Branch selector** — an editable ComboBox in the commit window. Typing an existing branch
  switches to it before committing; typing a new name creates it; the hint beside it says
  which, before you press anything. There is no separate "commit to a new branch" action
  because this is it.
- **Push, with guardrails** — a new branch asks once per repository before creating an
  upstream, being behind offers pull-then-push, and a **diverged branch is refused**. Force
  push is never offered.
- **Live-editable diff** — the right pane is a real editor. `Ctrl+S` writes the file back with
  its **original encoding, BOM and line endings**, atomically, and refuses if something else
  changed it since you opened it. A staged file shows a strip with one-click restage.
- **Switch branch** — fuzzy filter over local and remote branches. If Git refuses because of
  local changes, it says which files and offers stash-switch-restore as an explicit choice —
  it never stashes on your behalf, and it only ever restores the stash it created.
- **Clone** — clipboard prefill when what you copied really is a remote URL, a determinate
  progress bar parsed from Git's own output, and a cancel that removes the partial directory.
- **Safe staging defaults** — tracked changes ticked, **untracked files unticked**, files
  matching secret patterns unticked and flagged. This is the rule that keeps `.env`,
  `appsettings.Development.json`, `bin/` and stray dumps out of a hurried commit.
- **Pull (rebase)** — with `git submodule update` afterwards, but only when the repository
  actually has a `.gitmodules`.
- **A real CLI** — every action is reachable as `flick <verb>`, so it works from a terminal,
  a script, or a keyboard launcher just as well as from Explorer.
- **Quick commit** — `Ctrl+Alt+G` opens a small popup at the cursor, on the repository the
  foreground Explorer window is showing. Enter commits and pushes, `Shift+Enter` commits,
  `Details…` hands the already-computed status to the full window without re-running Git, and
  clicking away dismisses it. A folder that is not a repository gets the clone dialog instead.
- **AI commit messages** — Anthropic or OpenAI, streamed into the box as they arrive. Press Enter
  before the message lands and the commit is *queued*: it fires the moment the text does. The diff
  is capped, lock files and generated code are excluded, secret-matching paths never leave the
  machine and anything that looks like a credential is redacted. The API key lives in Windows
  Credential Manager, never in a settings file. If any of it fails the box is an ordinary editable
  field with a one-line notice, and committing still works.
- **Resident service** — a tray process that pays WPF's startup cost once at logon and keeps
  the commit window and the popup pre-warmed. `flick.exe` forwards the verb over a per-user named pipe and
  exits; the window is on screen in **~25 ms** and populated in **~100 ms**, against ~900 ms
  for the same command with the service stopped. It is an optimisation, never a dependency:
  with nothing listening, the stub launches the app directly and everything still works.
- **Tray menu** — recent repositories, pause and resume the context menu, settings, quit.
  `flick autostart on` registers a logon task with a 45-second delay, so FlickGit never shows
  up in Windows' startup-impact list.

## What it deliberately does not do

FlickGit is not trying to replace a full Git client. It will never automatically run
`git reset --hard`, `git clean -fd`, `git checkout -- .`, `git branch -D` or
`git push --force`. It does not discard uncommitted work, and it does not rewrite a file's
encoding or line endings.

Status overlay icons (the green tick badges) are out of scope through Phase 6 — the
registration is machine-wide, Windows loads only about fifteen handlers, and doing it properly
needs a per-file status cache that would make Explorer slow.

---

## Quick start

**Requirements:** Windows 10 or 11, [Git for Windows](https://git-scm.com/download/win), and
the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

1. Download the release archive and extract it anywhere — `%LOCALAPPDATA%\Programs\FlickGit`
   is a good choice. Keep `FlickGit.exe`, `flick.exe`, `Resources\` and `icons\` together;
   the layout matters.
2. Register the context menu:

   ```powershell
   .\flick.exe install-shell
   ```

3. Right-click a folder inside a repository. On Windows 11 the entries live under
   **Show more options** (`Shift+F10`) — reaching the primary menu needs `IExplorerCommand`
   and a signed MSIX package, which is Phase 6.
4. Start the resident service, so windows open instantly:

   ```powershell
   .lick.exe autostart on     # at every logon, 45 s after sign-in
   .\FlickGit.exe tray          # or just for this session
   ```

To undo the integration: `.\flick.exe uninstall-shell`. It removes exactly the keys it
created and nothing else.

### Check the installation

```powershell
.\flick.exe diag doctor
```

Reports which `git.exe` was found, its version, whether the context menu is registered,
whether the logon task and the resident service are running, and where settings and logs live.

`flick diag timings` prints what the resident service has actually measured — per Git command
and per window — so the performance budgets in [CLAUDE.md](CLAUDE.md) are checked rather than
assumed.

---

## Command line

```text
flick commit <path>                 commit window
flick push <path>                   with the guardrails; exit 5 if refused
flick switch <path> [branch]        picker when omitted, direct switch when named
flick clone <path> [url]            clone into a subdirectory of <path>
flick pull-rebase <path>            pull --rebase (+ submodules when present)
flick pull-rebase-autostash <path>
flick status <path>
flick terminal <path>               open a terminal there
flick install-shell                 register the Explorer context menu
flick uninstall-shell
flick autostart [on|off]            start the resident service at logon
flick ai                            what the AI is configured to do
flick ai key [set|clear]            store or remove the API key
flick diag doctor                   environment health check
flick diag timings                  recent latency measurements
flick help
```

`<path>` defaults to the current directory. Verbs declared but not yet implemented
(`quick-commit`, `palette`, `settings`) say so rather than failing obscurely.

`push`, `status`, `switch <branch>` and the `diag` commands are text commands: run from a
terminal they print and set a real exit code, and run from Explorer they say the same thing in
a window.

**Exit codes:** `0` success · `1` Git error · `2` not a repository · `3` cancelled ·
`4` configuration error · `5` refused for safety.

---

## Settings

`%LOCALAPPDATA%\FlickGit\settings.json`, written the first time it is needed. A settings
window arrives in Phase 5; until then this file is the interface.

| Setting | Default | What it does |
|---|---|---|
| `gitPath` | *(empty)* | Override for `git.exe`. Empty searches `PATH`, then the standard install locations. |
| `primaryBranch` | *(empty)* | Empty resolves per repository: the remote's HEAD, then `main`, then `master`. |
| `warnWhenCommittingToPrimaryBranch` | `true` | Shows the warning strip in the commit window. |
| `closeCommitWindowAfterSuccess` | `true` | |
| `language` | *(empty)* | Two-letter code. Empty follows Windows. |
| `diffFontFamily` | `Cascadia Mono, Consolas, Courier New` | Must be monospaced, or the panes stop aligning. |
| `diffFontSize` | `12.5` | |
| `verboseLogging` | `false` | Adds per-command Git timings to the log. |
| `allowUpstreamCreation` | `{}` | Per-repository answer to "create an upstream?", remembered after it is asked once. |
| `trigger` | `Hotkey` | `Hotkey` or `None`. CLAUDE.md's Explorer-scoped input hooks are not built yet, so there is no value for them — a setting that silently falls back is worse than one that does not exist. |
| `hotkeyGesture` | `Ctrl+Alt+G` | At least one modifier is required. An unparseable value falls back to the default and is logged. |
| `paletteHotkeyGesture` | `Ctrl+Alt+R` | Opens the repository palette. Not `Ctrl+Alt+G`: one combination cannot be registered twice, and the quick-commit trigger has it. |
| `paletteScanRoots` | `[]` | Folders the palette searches for repositories, three levels deep. Empty is fine — the palette also lists the ones you have already used. |

### Custom actions

`%LOCALAPPDATA%\FlickGit\actions.json` adds entries to the context menu, the palette and the CLI at
once. It is hand-edited until the settings window arrives.

```json
{
  "schemaVersion": 1,
  "actions": [
    {
      "id": "custom.fetch-prune",
      "label": "Fetch (prune)",
      "run": { "type": "git", "args": ["fetch", "--prune", "{remote}"] },
      "surfaces": ["menu", "palette"],
      "requiresRepo": true,
      "menuOrder": 145,
      "inMore": true,
      "showOutput": "window"
    }
  ],
  "builtIns": {
    "status": { "hidden": true },
    "push":   { "menuOrder": 115 }
  }
}
```

- `run.type` is `git`, `process`, `window` or `composite`. Arguments are always a **list**, never a
  command string — nothing is passed through a shell.
- Placeholders: `{repo}`, `{branch}`, `{upstream}`, `{remote}`, `{selection}`, and `{files}`, which
  expands to one argument per file. A path with a space in it stays one argument.
- `surfaces` is any of `menu`, `palette`. Absent or unrecognised means both — an action nobody can
  see is a worse answer to a typo than one in a place too many. The CLI is not a surface: it reaches
  any action by id with `flick run`.
- `requiresRepo` refuses the action outside a working tree. The context menu cannot check it (a
  registry verb is shown on every folder), so there it runs and reports the reason.
- `builtIns` hides, relabels or reorders a shipped entry. Built-ins are never deleted, so a hidden one
  can be put back.
- **Anything destructive is confirmed whether the file asks for it or not.** `reset --hard`,
  `clean`, `push --force`, `branch -D`, `checkout -- .`, `restore .` and every `process` action are
  forced to ask, showing the exact expanded command first.
- A bad entry is skipped with the reason logged and named in `flick diag doctor`; the built-ins keep
  working.
| `aiProvider` | `anthropic` | `anthropic`, `openai` or `disabled`. Inert until `aiAllowDiffsToLeaveMachine` is true. |
| `aiModel` | *(empty)* | Empty means the provider's default. |
| `aiMaxDiffBytes` | `12288` | Above this the payload becomes a file summary plus 40 lines per file. |
| `aiConventionalCommits` | `false` | On requires Conventional Commits; off leaves it to the model. |
| `aiAllowDiffsToLeaveMachine` | `false` | **Nothing is sent until this is true.** Asked once, on first use. |

API keys are never written here — Windows Credential Manager or DPAPI only, from Phase 4.

Logs: `%LOCALAPPDATA%\FlickGit\Logs\flickgit.log`, rotated at 2 MB with one generation kept.
They record Git command names, durations and exit codes — never diffs, file contents, commit
message bodies or credentials.

---

## Building

```powershell
dotnet build FlickGit.sln
dotnet test
```

The .NET 9 SDK is the only requirement. Producing the release layout additionally needs the
MSVC linker, because `flick.exe` is compiled with Native AOT:

```powershell
dotnet publish src/FlickGit.App/FlickGit.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o artifacts/FlickGit
dotnet publish src/FlickGit.Cli/FlickGit.Cli.csproj -c Release -r win-x64 -o artifacts/FlickGit
```

Icons are generated rather than committed as opaque binaries, so a change to a glyph is a
diff someone can review:

```powershell
pwsh tools/make-icons.ps1
```

### Layout

```text
src/
├── FlickGit.Cli/     Native AOT stub -> flick.exe. Parses a verb, starts the app, exits.
│                     No Git logic, no dependencies. Budget: 30 ms start to exit.
├── FlickGit.App/     WPF -> FlickGit.exe. Tray icon, commit window, diff viewer,
│                     registry integration, verb dispatch.
└── FlickGit.Core/    net9.0, no UI dependency, enforced by an MSBuild target.
                      Git process runner, repository detection, status and numstat
                      parsing, diff, commit, pull, branch, the command-line grammar.
tests/
└── FlickGit.Core.Tests/   The only test project. Parsers, the commit sequence, the
                          safety rules, the working tree, the command-line grammar.
```

**Why two executables.** A framework-dependent .NET stub costs 50–100 ms of CLR startup
before it does anything, which would defeat the point of having a resident process at all.
`flick.exe` is Native AOT and does nothing but forward. `FlickGit.exe` pays WPF startup once
and stays up.

**Why `FlickGit.Core` has no UI reference.** It is enforced by a build target
(`FLICK0001`), not by review. The working-tree editing and diff code is the most dangerous
part of the product and it has to stay testable without a message pump.

**Why the suite is small.** It covers `FlickGit.Core` and nothing else — the parsers, the
commit sequence, the safety rules, the working-tree round trips and the command-line grammar.
There is no WPF test project: a test that has to construct a `Window` is testing WPF, and the
resident service is verified by running it. Everything in `FlickGit.App` takes its
dependencies as typed constructor parameters so that stays a choice rather than a limitation
— `App.xaml.cs` is the only file that mentions the container. Both rules are written down in
[CLAUDE.md](CLAUDE.md) under **Hard Requirements**.

---

## Roadmap

| Phase | | |
|---|---|---|
| **1** | The commit path | ✅ Repository detection · Git runner · registry menu · commit window · file list with line counts · stage/unstage · side-by-side diff · commit · pull --rebase · error handling · logging |
| **2** | Branches, push, live editing | ✅ Branch ComboBox with switch/create · branch validation · push with upstream handling and divergence refusal · Switch branch with stash-switch-restore · Clone with clipboard prefill and progress · **editable right pane** with encoding and line-ending preservation · atomic save · external-modification detection · restage prompt |
| **3** | Speed | ✅ Resident service with tray menu and MRU · named-pipe IPC with direct-launch fallback · pre-warmed windows · foreground activation · logon task · diff prefetch cache · notifications |
| **6** | Deep shell integration | ✅ Line and hunk staging — stage or unstage a hunk, or just the lines you select, from the diff pane; the patch is generated from the in-memory diff with the file's own line endings and applied with `git apply --cached`. *Windows 11 primary menu (`IExplorerCommand` + sparse MSIX) skipped: it needs a signing certificate.* |
| **5** | Customisation | ✅ Repository palette (`Ctrl+Alt+R`) with fuzzy filtering, action mode, branch completion and `Ctrl+Enter` to pull everything behind · Action Catalog with `actions.json`, projected onto the context menu, the palette and the CLI · `flick run <id>` · `diag` commands. *No settings window by choice — `settings.json` and `actions.json` are the interface. Ollama, and with it speculative generation, deliberately skipped.* |
| **4** | Quick commit and AI | ✅ Global hotkey trigger · `IShellWindows` folder resolution with tab ambiguity · cursor-anchored quick-commit popup · queued Enter · streaming AI commit messages (Anthropic / OpenAI) · diff capping and redaction · key in Credential Manager. *Explorer-scoped input hooks still to come; they fall back to the hotkey.* |
| **5** | Customisation | Action Catalog · settings window · menu customisation · repository palette · Ollama · `diag` expansion |
| **6** | Deep shell integration | Sparse MSIX · `IExplorerCommand` for the Windows 11 primary menu · repository-aware `GetState` · line and hunk staging |

Design decisions, performance budgets and the reasoning behind them live in
[CLAUDE.md](CLAUDE.md).

---

## Licence

MIT. Third-party dependencies, all MIT: [AvalonEdit](https://github.com/icsharpcode/AvalonEdit)
(diff editor), [DiffPlex](https://github.com/mmanela/diffplex) (line and word diff),
[H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (tray icon without WinForms).
