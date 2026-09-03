# The macOS port — state, and what to do next

Written for whoever picks this up **on a Mac with Xcode**. Everything below was built and verified
from Windows, which is the whole reason this document exists: a large amount of it compiles, and a
smaller amount of it has ever been *looked at*.

Read [CLAUDE.md](../CLAUDE.md) first. It is the design document and it still governs; this file only
covers what is different about macOS and what is unfinished.

---

## 1. Where the code is

| Project | What it is | TFM |
|---|---|---|
| `src/FlickGit.Core` | Git, diffing, parsers, safety rules. The only tested assembly. | `net9.0` |
| `src/FlickGit.App.Common` | The platform-independent half of the app: verb routing, all view models, string table, settings. | `net9.0` |
| `src/FlickGit.App.Mac.Platform` | The macOS facilities behind Common's seams: launchd, Trash, Keychain, socket, hotkey. | `net9.0` |
| `src/FlickGit.App.Mac` | Avalonia windows and the resident service. Ships as the binary inside `FlickGit.app`. | `net9.0` |
| `src/FlickGit.App.Mac.Cli` | `flick` — answers text verbs, forwards the rest over the socket. | `net9.0` |
| `src/FlickGit.FinderSync` | The `FIFinderSync` extension. Swift. | — |
| `src/FlickGit.Setup.Mac` | `Info.plist` and `bundle.sh`, which assembles `FlickGit.app`. | — |

`src/FlickGit.App` is the **Windows** WPF app and `src/FlickGit.Shell` its COM shell extension.
Neither is used on macOS and neither should grow a macOS branch.

### Why four projects on the macOS side

Each has one job, and the split was forced rather than chosen:

- `App.Common` must stay platform-*independent*, so no macOS interop may go in it.
- The GUI and the CLI both need launchd, the Trash and the socket, and neither can reference the
  other — a CLI referencing the GUI would drag a UI toolkit into `flick status`, and the reverse is
  backwards. Hence `App.Mac.Platform`.

## 2. How to build and run it

```bash
# Tests. Run these first: they are the only tests in the product and they now pass on macOS.
dotnet test tests/FlickGit.Core.Tests/FlickGit.Core.Tests.csproj

# The app bundle, both architectures.
for rid in osx-arm64 osx-x64; do
  for p in FlickGit.App.Mac FlickGit.App.Mac.Cli; do
    dotnet publish "src/$p/$p.csproj" -c Release -r $rid --self-contained false -o "artifacts/$rid"
  done
done

src/FlickGit.Setup.Mac/bundle.sh 0.1.0 artifacts/osx-arm64 artifacts/osx-x64 artifacts/mac
open artifacts/mac/FlickGit.app
```

`.github/workflows/build.yml` has a `macos` job that does exactly this on every push, so the
commands above are kept honest by CI rather than by this document.

### The architecture, in one paragraph

`flick` is the command. It answers text verbs itself, and forwards anything else to the resident
`FlickGit` process over a Unix socket — falling back to doing the work locally when nothing is
listening. That mirrors the Windows stub-plus-resident split, and CLAUDE.md's rule holds: **the
resident service is an optimisation, never a dependency.** The Finder extension launches `flick`; it
never talks to the service directly.

## 3. What works

- **`flick` text verbs**: `status`, `log`, `add`, `rm`, `repo`, `version`, `help`, `language`,
  `diag timings`, `autostart`.
- **Seven Avalonia windows**: commit (with the editable diff pane, hunk staging, revert, find bar,
  overview strip), palette, log, branch picker, tags, stashes, submodules.
- **The resident service**: socket at `$TMPDIR/flickgit-<user>/service.sock`, mode `0600` in a `0700`
  directory, peer uid checked with `getpeereid`.
- **Keychain, Trash, launchd, the Carbon hotkey** — all written; see §5 for which of them have
  actually been *executed*.

## 4. What is missing — the work list

### 4a. Nine verb surfaces still refused

Each raises `HostCapabilityException`, which `VerbRunner` turns into exit code 4 and a sentence
naming the verb. Grep for it to find them:

| Verb | Where to implement | Notes |
|---|---|---|
| `blame` | `MacWindowVerbs.BlameAsync` | Needs a `BlameMargin` port — see `src/FlickGit.App/Rendering/BlameMargin.cs`. The WPF one draws a four-column gutter with `DrawingContext`; `AbstractMargin` survives in AvaloniaEdit, so this is the same port `DiffLineNumberMargin` already had. |
| `pr` | `MacWindowVerbs.PullRequestAsync` | `PullRequestFlow` in Core owns the sequence. The window is a summary plus two text boxes. |
| `pull-rebase` | `MacWindowVerbs.PullAsync` | A progress window over `PullService`. |
| `back` | `MacWindowVerbs.BackAsync` | Added on the Windows side after the macOS host existed; see `src/FlickGit.App/CommandLine/WindowVerbs.cs`. |
| `repo` | `MacWindowVerbs.Repo` | `RepositoryConfigService` + `RemoteService`. Remember `RemoteService.SaveAsync` owns the rename-before-set-url order. |
| `clone` | `MacWindowVerbs.Clone` | `CloneService`. Parse `--progress` off stderr for a determinate bar; cancellation must kill the process tree **and** delete only a directory this operation created. |
| `settings` | `MacEnvironmentVerbs.Settings` | Also needs `MarkdownFlow` — see below. |
| `ai` | `MacEnvironmentVerbs.AiAsync` | `KeychainSecretStore` is written and registered; what is missing is the window that asks for a key. |
| `diag doctor` | `MacEnvironmentVerbs.DoctorAsync` | **Not a port.** Most of what the Windows one reports is the registry, the overlay slot limit and the input trigger. A macOS doctor is a different report: git location, the socket, the LaunchAgent, whether the Finder extension is enabled, Automation and Accessibility permissions. |

**`MarkdownFlow`** (`src/FlickGit.App/Rendering/MarkdownFlow.cs`, 422 lines) has no Avalonia
equivalent — there is no `FlowDocument`. Its only consumer is the settings window's Help tab, which
is why it is listed with `settings` rather than on its own.

### 4b. Two windows have no view model, and that is the shape of the remaining work

Commit and palette were the easy ones: `CommitViewModel` (2,032 lines) and `PaletteViewModel` were
already framework-free and moved to `App.Common` unchanged, so those windows are bindings over
existing logic. **None of the nine above has a view model.** Their Windows logic lives in WPF
code-behind (`LogWindow.xaml.cs` is 664 lines, `SwitchBranchWindow.xaml.cs` is 822), so each is a
port of *behaviour*.

The macOS ones written so far did not port that code-behind — they were written thin, directly over
the Core services, because Core already holds every sequence and every safety rule. That is the
cheaper and safer route and is the recommended one: read the WPF window for the *rules*, then write
the Avalonia window against `FlickGit.Core`.

### 4c. Notarisation — the one hard gate

`bundle.sh` signs **ad-hoc** unless `MACOS_SIGN_IDENTITY` is set. Ad-hoc is enough for
`codesign --verify` to accept the bundle's structure and for the app to run locally after the usual
Gatekeeper warning. It is **not** enough for Finder to load the extension: a Finder Sync extension
requires a notarised Developer ID signature.

So until someone has a Developer ID ($99/yr):

- The context menu and the repository badge **cannot be tested at all**. Not "will look wrong" —
  Finder will not load the extension.
- Everything else (the CLI, the windows, the service, the hotkey) works unsigned.

To finish it: build with `MACOS_SIGN_IDENTITY="Developer ID Application: …"`, then
`xcrun notarytool submit --wait`, then `xcrun stapler staple artifacts/mac/FlickGit.app`. Add the
identity and an App Store Connect key as repository secrets and the CI job can do it.

## 5. What has never run on a Mac

This is the most important section in the document.

Everything was written on Windows. The `net9.0` TFM means the same IL runs on both, and a great deal
was verified by *running it on Windows* — the Avalonia app starts, builds its DI graph, constructs
every window, serves the socket, and answers `flick`. But:

**Never executed anywhere:**

- `KeychainSecretStore` — Security.framework interop. The asymmetry to watch: the `kSec*` names are
  `CFStringRef` **globals**, so the export address must be dereferenced once, while
  `kCFTypeDictionaryKeyCallBacks` is the **struct itself**, so its export address *is* the argument.
  Getting that backwards silently matches nothing.
- `FinderTrash` — `NSFileManager.trashItem` through `objc_msgSend`. Each `objc_msgSend` has its own
  declaration on purpose: it is not an ordinary variadic function, and one declaration reused across
  signatures passes arguments in the wrong registers and returns nonsense rather than failing.
  **Test it by deleting an untracked file and checking Finder offers "Put Back"** — a plain move to
  `~/.Trash` would look identical and be wrong.
- `LaunchAgentAutostart` — the plist is built with `XDocument` because an executable path containing
  `&` would otherwise produce a plist launchd refuses to parse. `KeepAlive` is deliberately `false`.
- `GlobalHotkey` — Carbon `RegisterEventHotKey`. Chosen over an `NSEvent` global monitor because the
  monitor needs Accessibility consent and this needs none.
- **The `getpeereid` peer check.** Windows has AF_UNIX but no `getpeereid`, so on a non-macOS host
  `LocalEndpoint.IsSameUser` logs that it did not check and returns true. The security model of the
  local endpoint is therefore **written and unproven**. It matters: a request on that socket can
  start a process through a user-defined action.
- The `0700`/`0600` modes, likewise skipped off macOS by `Restrict`.

**Never rendered:**

Every pixel. Compiled bindings mean a binding *path* is a build error (`AVLN2000`), and every window
has been *instantiated* without throwing — but nothing about layout, spacing, colours, scroll feel or
whether the find bar is usable has been seen by anyone.

### The first hour on a Mac, in order

1. `dotnet test` — confirms the Core path fixes.
2. `bundle.sh`, then `open FlickGit.app`, then `flick commit .` — and **look at the window**.
3. Save an edit through the diff pane on a **case-sensitive** APFS volume. Two things to check
   specifically: that the save is not refused, and that saving a `chmod +x` script leaves
   `git status` showing **no** mode change. Both were bugs; both are fixed; neither has been observed
   working.
4. Delete an untracked file from the commit window and confirm Finder offers "Put Back".
5. `flick ai key set` — the first exercise of the Keychain interop.
6. `flick autostart on`, log out and back in.

## 6. Gotchas that cost time here

- **No editable `ComboBox` in Avalonia.** The branch picker uses `AutoCompleteBox`.
- **`TextBox.Watermark` is obsolete** in Avalonia 12; it is `PlaceholderText`.
- **`Dispatcher.UIThread.InvokeAsync`** returns `DispatcherOperation<T>` for a synchronous lambda —
  `.GetTask()` — and unwraps to `Task<T>` for an async one.
- **`IDialogs.ConfirmAsync` is async because Avalonia leaves no choice.** WPF's `ShowDialog()` blocks;
  there is no synchronous equivalent, and faking one with a nested message loop is how a modal dialog
  re-enters the handler that opened it.
- **Both macOS composition roots use `ValidateOnBuild = true`.** Keep it. A missing registration
  otherwise kills the resident service on its first request, and every later `flick` silently falls
  back to the CLI's refusals — which looks exactly like a window that was never wired.
- **`.gitattributes` pins shell, Swift, plists and workflow YAML to LF.** A CR in `bundle.sh` is
  `\r: command not found`, and the workflow YAML holds bash `run:` blocks.
- **Read the CI annotations, not the log.** Downloading a job log needs a repository token;
  annotations do not. The test step re-emits its failures as an annotation for that reason.

## 7. What the CI job already caught

Worth knowing, because it is the argument for running things rather than reading them. The first real
macOS run found a bug that four careful passes over the path-handling code had missed:

`SubmoduleService.Normalise` trimmed separators from **both** ends. On Unix the leading separator is
what makes a path absolute, so `/somewhere/else` arrived at the containment check as the relative
`somewhere/else`, resolved inside the repository, and `SubmoduleRefusal.OutsideRepository` never
fired — a submodule could be declared outside the repository it belongs to. Windows was fine only by
accident: an absolute path there begins with a drive letter, which has no separator to lose.

The audit missed it because it read the *path* code, and the bug was in a caller that mangled the
path before the path code saw it.
