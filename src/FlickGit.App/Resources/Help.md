# FlickGit

Fast Git actions from Windows Explorer. Right-click, review, commit — without opening a full
Git client.

This page is a plain Markdown file sitting next to `FlickGit.exe`. Edit it, press **Reload**,
and what you wrote is what this tab shows.

---

## The fast path

1. Press **Ctrl+Alt+G** while an Explorer window is in front. The commit window opens on the
   repository that window is showing, with the caret already in the message box.
2. An AI commit message streams in, if AI is configured. Or type your own.
3. Press **Enter** to commit and push.

Pressing Enter before the message has arrived is fine — the commit is queued and fires the
moment it lands. **Esc** closes the window at any point up until the commit actually starts
running, and whatever the AI was doing is abandoned with it.

**Shift+Enter** is a newline, for a commit body. **Ctrl+S** saves an edit in the diff pane, and
Enter there is an ordinary newline in your file rather than a commit.

If no Explorer window is in front, **nothing happens** — FlickGit will not guess which
repository you meant. Use **Ctrl+Alt+R**, or the tray icon's Recent list.

**Ctrl+Alt+R** opens the repository palette instead: every repository FlickGit knows about,
the ones with something to do listed first. Type to filter, type a space to switch to actions,
`Ctrl+Enter` to pull every repository that is behind.

## The Explorer context menu

Right-click a folder — the repository root, any folder inside it, or the empty background of a
folder you are browsing.

**Commit / Push** shows the branch you are on — `Commit / Push (main)…` — and both top-level
entries disappear on a folder that is not a Git repository. That needs `FlickGit.Shell.dll` next to
`flick.exe`; without it the entries still work, they are just plain labels that always show.

Replacing that DLL needs Explorer restarted, because Explorer keeps it loaded once it has drawn a
menu with it. Turning the menu off in **Settings** takes effect immediately either way.

On Windows 11 the entries live under **Show more options** (Shift+F10). That is a limitation of
registry context menus, not a setting: the Windows 11 top-level menu needs a signed package.

- **Commit / Push…** and **Pull (rebase)** are entries in the menu itself, at the bottom, one
  click from the right-click. Pull runs a submodule update afterwards when the repository has a
  `.gitmodules`.
- **FlickGit ▸** holds the rest: Switch branch…, Tags…, Push, Clone…, Repository status, Open
  terminal here, and any action you added yourself.

**Clone…** lives in that submenu rather than replacing the menu outside a repository: a registry
entry is written once and shown on every folder, so it cannot tell where you clicked. It prefills
the URL from your clipboard when what you copied really is a remote URL, and the entries that need
a repository say so when there is none.

## The commit window

The tick boxes decide the commit — nothing else does.

- Tracked changes are ticked for you.
- **Untracked files are not**, and neither is anything matching a secret pattern. This is the
  rule that keeps `.env`, `appsettings.Development.json` and stray dumps out of a hurried
  commit. Tick one if you mean it.
- The **branch box** is an editable combo. Leave it alone to commit where you are, type an
  existing branch to switch first, or type a new name to create it. The hint beside it says
  which, before you press anything.

### The diff pane

The right side is a real editor, not a preview.

- **Ctrl+S** writes the file back with its original encoding, BOM and line endings, atomically.
  If something else changed the file since you opened it, the save is refused and you are
  offered a reload.
- Select lines, or a whole hunk, and stage just those. The same patch reversed unstages them.
- **Revert lines** puts the left pane's version of the selected lines back. It is an edit, not a Git
  operation: **Ctrl+Z** undoes it, and nothing reaches the disk until you press **Ctrl+S**.
- The header always says what you are comparing against: `Working tree ↔ HEAD` or
  `Working tree ↔ Index`. Editing while looking at the staged diff edits the **working tree** —
  a strip appears offering to restage.

## Commit messages by AI

Off until you turn it on, because it means a diff leaves your machine.

Everything you need is in **Settings**, under *Commit messages (AI)*:

1. Pick who writes them — **Anthropic** (Claude Haiku 4.5) or **OpenAI** (GPT-5.6 Luna).
2. Press **Set API key…** and paste your key. It goes into Windows Credential Manager, never
   into a settings file, and is never shown back to you.
3. Tick **Allow the diff to be sent to this provider**. Nothing leaves your machine until you do.

Press **Save**. The commit window then grows a **Generate with AI** button, and writes a message
for you as soon as it opens.

The same thing from a terminal:

```
flick ai              what it is configured to do
flick ai key set      store the API key (Windows Credential Manager)
flick ai key clear    remove it
```

`aiModel`, `aiMaxDiffBytes` and `aiConventionalCommits` in `settings.json` are the rest of it. The
diff is capped before it is sent, lock files and generated code are excluded, and anything matching
a secret pattern is redacted or dropped.

The AI is never a dependency. If it is slow, unreachable or unconfigured, the message box is an
ordinary text box and every button still works.

## Command line

Everything in the menus is also a verb. `<path>` defaults to the current directory.

```
flick commit <path>              flick switch <path> [branch]
flick pull-rebase <path>         flick tag <path> [name]
flick push <path>                flick status <path>
flick palette                    flick clone <path> [url]
flick settings                   flick terminal <path>
                                 flick run <id> [path]

flick install-shell              register the context menu
flick uninstall-shell            remove it
flick autostart [on|off]         start with Windows
flick language [code|auto]       interface language
flick diag doctor                environment health check
flick diag timings               recent latency measurements
```

Exit codes: `0` success, `1` Git error, `2` not a repository, `3` cancelled, `4` configuration
error, `5` refused for safety.

## Configuration files

Everything this window does not show lives in two files, in
`%LOCALAPPDATA%\FlickGit`:

- **`settings.json`** — hotkeys, diff font, AI provider and model, palette scan roots, the
  primary-branch override.
- **`actions.json`** — your own context-menu and palette entries, and overrides that hide,
  rename or reorder the built-in ones.

Both are read at startup. Restart FlickGit after editing them, and run `flick diag doctor` to
see what they resolved to.

## What FlickGit will not do

By design, and not configurable:

- No `reset --hard`, `clean -fd`, `checkout -- .` or `branch -D` without you asking for it in
  the moment.
- No force push, ever, and never as part of a commit.
- No automatic stash. If a branch switch is blocked, you are told which files block it and
  offered stash-switch-restore as an explicit choice — and only the stash it created is ever
  restored.
- No `git add -A`. Only the paths you ticked.

## When something is wrong

Run `flick diag doctor`. It reports where `git.exe` is, whether the context menu and the logon
task are registered, whether the resident service is running, which hotkeys were claimed, what
the AI is configured to do, and which language file is actually in use.

Logs are in `%LOCALAPPDATA%\FlickGit\Logs`. They never contain diffs, file contents, commit
message bodies or API keys.

---

FlickGit is by **o0Zz** — [github.com/o0Zz/FlickGit](https://github.com/o0Zz/FlickGit/)
