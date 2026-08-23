# ⚡ FlickGit

**Fast Git actions, right from Windows Explorer.**
Right-click, review, commit — without opening a full Git client.

FlickGit is built for developers who work across multiple repositories and switch between them constantly. It keeps the Git actions you use every day fast, lightweight, and instantly accessible — directly from Explorer.

---

## Features

- [x] **Explorer context menu** — Commit / Push… and Pull (rebase) as entries of their own at the
      bottom of the menu, the rest in the *FlickGit* submenu. Per-user, no administrator rights.
- [x] **The branch in the menu** — `Commit / Push (feature/storage-gw)…`, read from `.git/HEAD` while
      the menu is built, and both root entries hide themselves on a folder that is not a repository.
      Needs `FlickGit.Shell.dll` beside `flick.exe`; without it the entries are plain static verbs.
- [x] **One-key commit** — `Ctrl+Alt+G` opens the commit window on the repository the front
      Explorer window is showing, caret already in the message box. Enter commits and pushes;
      `Shift+Enter` is a newline. With no Explorer window in front, nothing opens — FlickGit does
      not guess which repository you meant.
- [x] **Live-editable diff** — the right pane is the file on disk. Edit it, stage or unstage
      individual lines, or **revert** selected lines to the left side's version. Reverting is an
      editor edit: `Ctrl+Z` undoes it and nothing is written until `Ctrl+S`, which restores the
      file's original encoding, BOM and line endings.
- [x] **Repository palette** — `Ctrl+Alt+R`, repositories with something to do listed first, fuzzy
      filter, action mode, `Ctrl+Enter` to pull every repository that is behind.
- [x] **Commit window** — file list with status letters and `+added / -removed` counts, and tick
      boxes that are the only thing deciding what is committed.
- [x] **Safe staging defaults** — tracked changes ticked, **untracked files unticked**, anything
      matching a secret pattern unticked and flagged.
- [x] **Branch box** — type an existing branch to switch before committing, a new name to create
      it; the hint beside it says which before you press anything.
- [x] **Side-by-side diff, live-editable** — `Ctrl+S` writes the file back with its original
      encoding, BOM and line endings, atomically, and refuses if something else changed it.
- [x] **Line and hunk staging** — stage or unstage a whole hunk, or just the lines you select.
- [x] **AI commit messages** — Anthropic or OpenAI, streamed as they arrive. Enter before the
      message lands queues the commit. Capped diff, secrets redacted, key in Credential Manager.
- [x] **Push with guardrails** — asks once per repository before creating an upstream, offers
      pull-then-push when behind, and **refuses a diverged push**. Force push is never offered.
- [x] **Switch branch** — fuzzy filter over local and remote branches. If Git refuses, it names
      the files and offers stash-switch-restore as an explicit choice.
- [x] **Tags** — list, create, publish and delete in one window. Nothing is forced, and deleting
      always asks first.
- [x] **Clone** — clipboard prefill when what you copied really is a remote URL, determinate
      progress, and a cancel that removes the partial directory.
- [x] **Pull (rebase)** — with `git submodule update` afterwards, but only when the repository
      actually has a `.gitmodules`.
- [x] **Resident service** — a tray process that pays WPF startup once at logon and keeps the
      windows pre-warmed: **~25 ms** to on screen against ~900 ms cold. An optimisation, never a
      dependency — with it stopped, everything still works.
- [x] **Custom actions** — `actions.json` adds entries to the context menu, the palette and the
      CLI at once, and hides, relabels or reorders the built-in ones.
- [x] **Settings window** — `flick settings`: the switches whose JSON key nobody can guess before
      finding the file, plus a Help tab rendering an editable `Help.md`, and About.
- [x] **A real CLI** — every action is `flick <verb>`, with real exit codes, so scripts and
      keyboard launchers reach it as easily as Explorer does.
- [x] **Six interface languages** — English, German, Spanish, French, Italian, Portuguese.
- [ ] **Windows 11 primary menu** — needs `IExplorerCommand` and a signed sparse MSIX package.
      Until there is a code-signing certificate the entries live under *Show more options*.
- [ ] **Explorer-only key or mouse trigger** — an input hook that fires only over Explorer, so a
      key like F12 keeps working everywhere else. The global hotkey is the shipped default.
- [ ] **Ollama, and speculative generation with it** — *not planned.* Anthropic and OpenAI cover
      the feature, and speculative generation may only run against a local provider.
- [ ] **Status overlay icons** — *not planned.* Registration is machine-wide, Windows loads only
      about fifteen handlers, and doing it properly needs a per-file status cache that would make
      Explorer slow.

**What it will never do:** run `git reset --hard`, `git clean -fd`, `git checkout -- .`,
`git branch -D` or `git push --force` on its own, discard uncommitted work, or rewrite a file's
encoding or line endings.

---

## Quick start

**Requirements:** Windows 10 or 11, [Git for Windows](https://git-scm.com/download/win), and
the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

1. Download the release archive and extract it anywhere — `%LOCALAPPDATA%\Programs\FlickGit`
   is a good choice. Keep the whole layout together: `FlickGit.exe`, `flick.exe`, `Help.md`,
   `Resources\` and `icons\`. The registry entries name `flick.exe` and `icons\*.ico` by path.
2. Register the context menu:

   ```powershell
   .\flick.exe install-shell
   ```

3. Right-click a folder inside a repository. On Windows 11 the entries live under
   **Show more options** (`Shift+F10`) — reaching the primary menu needs `IExplorerCommand`
   and a signed MSIX package, which is Phase 6.
4. Start the resident service, so windows open instantly:

   ```powershell
   .\flick.exe autostart on     # at every logon, 45 s after sign-in
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
flick pull-rebase <path>            pull --rebase (+ submodules when present)
flick pull-rebase-autostash <path>
flick push <path>                   with the guardrails; exit 5 if refused
flick switch <path> [branch]        picker when omitted, direct switch when named
flick tag <path> [name]             tag window when omitted, creates it when named
flick status <path>
flick terminal <path>               open a terminal there
flick clone <path> [url]            clone into a subdirectory of <path>
flick run <id> [path]               run a catalog action by id
flick palette                       repository palette
flick settings                      settings, help and about
flick install-shell                 register the Explorer context menu
flick uninstall-shell
flick autostart [on|off]            start the resident service at logon
flick ai                            what the AI is configured to do
flick ai key [set|clear]            store or remove the API key
flick language [code|auto]          interface language; lists them when omitted
flick diag doctor                   environment health check
flick diag timings                  recent latency measurements
flick help
```

`<path>` defaults to the current directory. `flick tray` starts the resident service in the
foreground and `flick version` prints the build; neither is worth a menu entry.

`push`, `status`, `switch <branch>` and the `diag` commands are text commands: run from a
terminal they print and set a real exit code, and run from Explorer they say the same thing in
a window.

**Exit codes:** `0` success · `1` Git error · `2` not a repository · `3` cancelled ·
`4` configuration error · `5` refused for safety.

---

## Settings

`%LOCALAPPDATA%\FlickGit\settings.json`, written the first time it is needed. `flick settings`
covers the handful of switches whose JSON key nobody can guess before they have found the file;
everything below is the file itself.

| Setting | Default | What it does |
|---|---|---|
| `gitPath` | *(empty)* | Override for `git.exe`. Empty searches `PATH`, then the standard install locations. |
| `primaryBranch` | *(empty)* | Empty resolves per repository: the remote's HEAD, then `main`, then `master`. |
| `warnWhenCommittingToPrimaryBranch` | `true` | Shows the warning strip in the commit window. |
| `closeCommitWindowAfterSuccess` | `true` | |
| `language` | *(empty)* | Interface language: `en`, `de`, `es`, `fr`, `it` or `pt`. Empty follows Windows. Set it with `flick language <code>` rather than by hand — the verb lists what is embedded and refuses a code that is not. A code with no language file falls back to English, and `flick diag doctor` says so. |
| `diffFontFamily` | `Cascadia Mono, Consolas, Courier New` | Must be monospaced, or the panes stop aligning. |
| `diffFontSize` | `12.5` | |
| `verboseLogging` | `false` | Adds per-command Git timings to the log. |
| `allowUpstreamCreation` | `{}` | Per-repository answer to "create an upstream?", remembered after it is asked once. |
| `trigger` | `Hotkey` | `Hotkey` or `None`. CLAUDE.md's Explorer-scoped input hooks are not built yet, so there is no value for them — a setting that silently falls back is worse than one that does not exist. |
| `hotkeyGesture` | `Ctrl+Alt+G` | At least one modifier is required. An unparseable value falls back to the default and is logged. |
| `paletteHotkeyGesture` | `Ctrl+Alt+R` | Opens the repository palette. Not `Ctrl+Alt+G`: one combination cannot be registered twice, and the commit trigger has it. |
| `paletteScanRoots` | `[]` | Folders the palette searches for repositories, three levels deep. Empty is fine — the palette also lists the ones you have already used. |
| `aiProvider` | `anthropic` | `anthropic`, `openai` or `disabled`. Inert until `aiAllowDiffsToLeaveMachine` is true. |
| `aiModel` | *(empty)* | Empty means the provider's default. |
| `aiReasoningEffort` | `none` | OpenAI only: `none`, `low` or `medium`. `none` is the latency baseline, which is the point of the tier. |
| `aiMaxDiffBytes` | `12288` | Above this the payload becomes a file summary plus 40 lines per file. |
| `aiConventionalCommits` | `false` | On requires Conventional Commits; off leaves it to the model. |
| `aiAllowDiffsToLeaveMachine` | `false` | **Nothing is sent until this is true.** Asked once, on first use. |
| `showSuccessNotification` | `true` | The toast after a commit, a pull or a push. |

API keys are never written here — Windows Credential Manager or DPAPI only.

### Custom actions

`%LOCALAPPDATA%\FlickGit\actions.json` adds entries to the context menu, the palette and the CLI at
once. It is hand-edited: the settings window covers the common switches, not the action list.

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

Logs: `%LOCALAPPDATA%\FlickGit\Logs\flickgit.log`, rotated at 2 MB with one generation kept.
They record Git command names, durations and exit codes — never diffs, file contents, commit
message bodies or credentials.

---

## Building

```powershell
winget install Microsoft.DotNet.SDK.9
dotnet build FlickGit.sln
dotnet test
```

The .NET 9 SDK is the only requirement. Producing the release layout additionally needs the
MSVC linker, because `flick.exe` is compiled with Native AOT:

```powershell
dotnet publish src/FlickGit.App/FlickGit.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o artifacts/FlickGit
dotnet publish src/FlickGit.Cli/FlickGit.Cli.csproj -c Release -r win-x64 -o artifacts/FlickGit
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
| **4** | The trigger and AI | ✅ Global hotkey trigger · `IShellWindows` folder resolution with tab ambiguity · queued Enter · streaming AI commit messages (Anthropic / OpenAI) · diff capping and redaction · key in Credential Manager. *The cursor-anchored quick-commit popup was built and then removed: the trigger opens the commit window instead, which took over the caret-in-the-message-box opening, Enter-commits and the streamed message. The Explorer-scoped input hooks — a key or a mouse side button swallowed only over Explorer — are still open; the setting falls back to the global hotkey and says so in `diag doctor`.* |
| **5** | Customisation | ✅ Repository palette (`Ctrl+Alt+R`) with fuzzy filtering, action mode, branch completion and `Ctrl+Enter` to pull everything behind · Action Catalog with `actions.json`, projected onto the context menu, the palette and the CLI · `flick run <id>` · `diag` commands · a small settings window (`flick settings`) carrying the context menu, autostart, the commit switches, the AI provider and its API key, the language picker, a Help tab rendering an editable `Help.md` and an About tab. *Three items were dropped on purpose: the drag-and-drop action editor (`actions.json` is the interface), the Ollama provider, and with it speculative generation — which by its own safety rule may only run against a local provider.* |
| **6** | Deep shell integration | ✅ Line and hunk staging — stage or unstage a hunk, or just the lines you select, from the diff pane; the patch is generated from the in-memory diff with the file's own line endings and applied with `git apply --cached`. ✅ `IExplorerCommand` handler (`FlickGit.Shell.dll`, Native AOT COM in `explorer.exe`) putting the current branch in the Commit label and hiding the root entries outside a repository — no MSIX or signature needed for the classic menu, only for the Windows 11 *primary* one. *The sparse MSIX package is the one thing still open: it needs package identity and a code-signing certificate.* |

Design decisions, performance budgets and the reasoning behind them live in
[CLAUDE.md](CLAUDE.md).

---

## Licence

MIT. Third-party dependencies, all MIT: [AvalonEdit](https://github.com/icsharpcode/AvalonEdit)
(diff editor), [DiffPlex](https://github.com/mmanela/diffplex) (line and word diff),
[H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (tray icon without WinForms).
