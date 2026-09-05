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
- **Every window**: commit (with the editable diff pane, hunk staging, revert, find bar, overview
  strip), palette, log, blame, changelog, clone, repository settings, pull request, settings, and
  the four pickers — branches, tags, stashes, submodules — at feature parity with the WPF ones. See
  `docs/features.md` for what that means, window by window.
- **The menu bar item**: recent repositories, Settings, About, Exit — and notifications, so an
  ordinary success is a banner rather than a window the user has to dismiss.
- **The global hotkeys**: Cmd+Alt+G opens the commit window on the folder Finder is showing,
  Cmd+Alt+R opens the palette.
- **The Finder menu**: the three root entries, the submenu, and a file-specific menu of Blame, Add
  and Remove from Git.
- **The resident service**: socket at `$TMPDIR/flickgit-<user>/service.sock`, mode `0600` in a `0700`
  directory, peer uid checked with `getpeereid`.
- **Keychain, Trash, launchd, the Carbon hotkey** — all written; see §5 for which of them have
  actually been *executed*.

## 4. What is missing — the work list

### 4a. Three verb surfaces still refused

Each raises `HostCapabilityException`, which `VerbRunner` turns into exit code 4 and a sentence
naming the verb. Grep for it to find them. **None of the three is a port**, which is why they are
still refused while the other eleven are not:

| Verb | Where | Why it is not a port |
|---|---|---|
| `install-shell` / `uninstall-shell` | `MacEnvironmentVerbs.ContextMenu` | The Windows verb writes a registry projection of the Action Catalog. The Finder menu is a Finder Sync extension inside the app bundle, enabled by the user in System Settings — there is no equivalent for a verb to write. |
| `install-overlay` / `uninstall-overlay` | `MacEnvironmentVerbs.OverlayAsync` | The badge is an `IShellIconOverlayIdentifier` under `HKLM`, elevation and a fifteen-handler limit. macOS has no counterpart; the Finder extension draws its own badges. |
| `diag doctor` | `MacEnvironmentVerbs.DoctorAsync` | Most of what the Windows one reports is the registry, the overlay slot limit and the input trigger. A macOS doctor is a different report: git location, the socket, the LaunchAgent, whether the Finder extension is enabled, Automation and Accessibility permissions. |

### 4b. Four things that are deliberately not the Windows answer

Not gaps. Each is a place where the platform makes a different mechanism right, and the difference
is worth stating so nobody "fixes" it back.

- **The Settings window has no Finder-menu checkbox and no badge checkbox.** Both are the verbs
  above: the extension is enabled in System Settings, and there is no `HKLM` half to elevate for.
- **The Finder menu does not carry user actions.** No *Fetch (prune)*, and no other `actions.json`
  entry: the Windows menu only has them because `install-shell` writes a projection of the catalog
  into the registry. A Finder Sync extension has nothing to write and reads no configuration —
  `flick run <id>` and the palette are how a user action is reached here.
- **Notifications go through `osascript display notification`.** `UNUserNotificationCenter` needs a
  signed bundle and an authorisation prompt; `NSUserNotification`, which needed neither, was removed
  in macOS 11. The banner works today and is attributed to the script runner rather than to FlickGit
  until §4c is done, at which point `MenuBarNotifier.Deliver` is the one method to replace.
- **No window is pre-warmed.** Pre-warming is the Windows host's answer to a 120 ms budget it has
  measured; nothing here has been measured on real hardware, and a pre-warmed window that has to be
  provably re-initialisable is a correctness cost to pay once there is a number saying it is needed.
  `MacWindowVerbs` says so at the top.

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
  monitor needs Accessibility consent and this needs none. Now wired: `App.StartHotkeys`.
- `FinderFolder` — the AppleScript that answers "which folder is Finder showing", and therefore the
  whole of what Cmd+Alt+G acts on. **It asks Finder rather than System Events**, so the user is
  prompted for one Automation target instead of two; the first press of a session carries that
  prompt, which is why the timeout is five seconds rather than tight against the 120 ms budget.
  Declining the prompt is a configuration, not a fault: the hotkey then opens nothing, which is the
  same outcome as pressing it with no Finder window in front.
- **The notification banner.** `osascript display notification`, started and abandoned. Worth
  checking twice: that a banner appears at all, and that a commit subject containing a double quote
  or a backslash arrives intact — `MenuBarNotifier.Quote` is the only escaping between a commit
  message and an AppleScript string literal.
- **The menu bar item.** Avalonia's `TrayIcon` onto `NSStatusItem`, with the `.ico` decoded by Skia
  for its image. If the image is the thing that fails, `MenuBar.LoadIcon` swallows it and the item
  is still there with its menu — that fallback has never been exercised either.
- **The `getpeereid` peer check.** Windows has AF_UNIX but no `getpeereid`, so on a non-macOS host
  `LocalEndpoint.IsSameUser` logs that it did not check and returns true. The security model of the
  local endpoint is therefore **written and unproven**. It matters: a request on that socket can
  start a process through a user-defined action.
- The `0700`/`0600` modes, likewise skipped off macOS by `Restrict`.

**Never rendered on a Mac:**

Every pixel. Compiled bindings mean a binding *path* is a build error (`AVLN2000`); every window has
been instantiated, measured and arranged without throwing; and each has been rendered offscreen on
Windows through `RenderTargetBitmap` and looked at, which is how the file rows turned out to be a
third taller than the Windows ones and how the pull-request window's delete-branch box turned out to
appear unlabelled on the refusal path.

What that does **not** cover is everything the native window supplies: the title bar and the traffic
lights, the menu bar, Retina backing scale, the system font actually resolving, scroll feel and
momentum, and whether the find bar is usable with a trackpad.

### The first hour on a Mac, in order

1. `dotnet test` — confirms the Core path fixes.
2. `bundle.sh`, then `open FlickGit.app`, then `flick commit .` — and **look at the window**.
3. Save an edit through the diff pane on a **case-sensitive** APFS volume. Two things to check
   specifically: that the save is not refused, and that saving a `chmod +x` script leaves
   `git status` showing **no** mode change. Both were bugs; both are fixed; neither has been observed
   working.
4. Delete an untracked file from the commit window and confirm Finder offers "Put Back".
5. **Press Cmd+Alt+G with a Finder window in front**, answer the Automation prompt, and confirm the
   commit window opens on *that* folder. Then press it with Finder behind something else and confirm
   nothing opens at all — that refusal is the rule, not a bug.
6. **Click the menu bar item**: the recent list, Settings, About, Exit. Then commit something and
   watch for the banner.
7. **Right-click a file in Finder**, and then a folder, and then a folder's background — three
   different menus, and the file one must be Blame/Add/Remove and nothing else.
8. `flick ai key set` — the first exercise of the Keychain interop.
9. `flick autostart on`, log out and back in.

## 6. Gotchas that cost time here

- **No editable `ComboBox` in Avalonia.** The branch picker uses `AutoCompleteBox`.
- **`TextBox.Watermark` is obsolete** in Avalonia 12; it is `PlaceholderText`.
- **A `ListBox` context menu needs `ContextRequested`, not the selection.** Avalonia *does* select on
  right-click, and that is not enough: it selects the one row under the pointer, silently collapsing
  a multi-selection the user built up to act on. `PickerList.SelectRowUnderPointer` restores the
  Windows rule — a click inside the selection means the selection, anywhere else means the row.
- **A platform guard has to be somewhere the analyser can see it.** `CA1416` is an error here, and an
  `OperatingSystem.IsMacOS()` check at the top of a method does not cover a lambda declared inside
  it. `App.StartHotkeys` is annotated and guarded at its call site instead.
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
