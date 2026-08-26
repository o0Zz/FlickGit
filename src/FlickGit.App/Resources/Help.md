# FlickGit

Fast Git actions from Windows Explorer. Right-click, review, commit — without opening a full
Git client.

Branches, worktrees and tags each have a window of their own. Everything is a keystroke or one
right-click away.

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
Enter there is an ordinary newline in your file rather than a commit. **F5** re-reads the
repository, the same as the Refresh button.

If no Explorer window is in front, **nothing happens** — FlickGit will not guess which
repository you meant. Use **Ctrl+Alt+R**, or the tray icon's Recent list.

**Ctrl+Alt+R** opens the repository palette instead: every repository FlickGit knows about,
the ones with something to do listed first. Type to filter, type a space to switch to actions,
`Ctrl+Enter` to pull every repository that is behind.

## The Explorer context menu

Right-click a folder — the repository root, any folder inside it, or the empty background of a
folder you are browsing.

**Commit / Push** shows the branch you are on — `Commit / Push (main)…` — and both top-level
entries disappear on a folder that is not a Git repository.

The whole block is drawn by `FlickGit.Shell.dll`, which lives beside `flick.exe`. Replacing that
file needs Explorer restarted, because Explorer keeps it loaded once it has drawn a menu with it —
which is what the installer does for you. Turning the menu off in **Settings** takes effect
immediately either way.

On Windows 11 the entries live under **Show more options** (Shift+F10). That is a limitation of
registry context menus, not a setting: the Windows 11 top-level menu needs a signed package.

- **Commit / Push…** and **Pull (rebase)** are entries in the menu itself, at the bottom, one
  click from the right-click. Pull runs a submodule update afterwards when the repository has a
  `.gitmodules`.
- **FlickGit ▸** holds the rest: Show log…, Branches…, Tags…, Push, Pull request…, Repository
  settings…, Clone…, Open terminal here, and any action you added yourself.
- Right-click a **file** rather than a folder and the submenu holds **Blame…**, which is all that
  applies to one.

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

## Branches, worktrees, tags and stashes

**FlickGit ▸ Branches…**, or `flick switch`. Type to filter; local branches first, then the
remote-tracking ones. Type a name that matches nothing and the last row offers to **create** it,
from HEAD. Right-click a row to delete it — `branch -d`, never `-D` unless a second question
explicitly asks for it, and deleting on a remote says so in its own words.

If Git refuses a switch, you are told which files block it and offered **stash, switch, restore**
as an explicit choice. Nothing is stashed for you, and only the stash FlickGit created is ever
restored.

### Worktrees

A worktree is a second checkout of the same repository, in its own folder, on its own branch — so
you can look at `main` without putting away what you are doing on your feature branch. Git allows
at most one per branch, which is why they live on the branch rows here rather than in a window of
their own.

Right-click a branch:

- **Create worktree…** — pick the parent folder; the leaf name is derived for you. It has to be
  outside the repository, or the new checkout would show up as untracked files.
- **Open folder** — the primary action on a row that already has one. Such a branch cannot be
  switched to in this checkout, and the row says `worktree` instead of `Local`.
- **Remove worktree…** — asks first. The folder is deleted; the branch and its commits stay. If
  there are uncommitted changes there, Git refuses and FlickGit does not offer to force it: delete
  the folder in Explorer instead, where it goes to the Recycle Bin.
- **Clean up missing worktrees…** — when the folder is gone but Git still records it, which is why
  that branch still cannot be switched to. It clears only records whose folder no longer exists.

### Tags

**FlickGit ▸ Tags…**, or `flick tag`. List, create, publish and delete, all in one window.
Nothing is ever forced, and deleting always asks first — the remote goes first, and no window open
costs a network round trip.

Double-click a tag to check it out. That is the one thing in FlickGit that **detaches HEAD**, so it
asks once and the window then tells you how to come back.

### Stashes

**FlickGit ▸ Stashes…**, or `flick stash`. What is put away, and the box at the bottom to put more
away. A message is optional; without one Git names the commit you were on. **Include untracked** is
ticked by default, so a file you have only just created goes in too and comes back with the rest —
anything your `.gitignore` already excludes is never included either way.

Right-click a row, or double-click it to pop:

- **Pop** — puts the changes back and removes the entry. It asks nothing, because it restores work
  rather than discarding any, and Git refuses outright rather than overwriting a file that is in the
  way. If it fails or leaves conflicts, **the stash is still in the list** — Git only removes it once
  it has applied cleanly.
- **Drop…** — throws the stash away without applying it, and asks first. A stash has no reflog, so
  FlickGit cannot bring one back.

There is no "drop all": one click that destroys every saved change in the repository is not something
this window offers. There is no `apply` either, because that is `pop` without the tidying up.

**Why the window sometimes says the list changed.** A stash is named by its position — `stash@{1}` is
whatever is second right now — and pushing or popping a stash anywhere renumbers the rest. So if you
stash something in a terminal while this window is open, the row you then click is no longer the stash
it was drawn as. FlickGit checks before it acts, does nothing, and reloads the list for you to pick
again. That check is also why popping and dropping have no command-line spelling: a position written
into a script is one that will have moved by the time it runs.

`flick stash <path> "a message"` does the one thing that is safe from a script: it puts the working
tree away, untracked files included, and prints what happened.

### Submodules

**FlickGit ▸ Submodules…**, or `flick submodule`. What the repository declares, whether
each one is checked out, and whether it has moved since the last commit.

- **Initialise** — a submodule that arrived with somebody else's commit and was never fetched. It
  runs the same `submodule update --init --recursive` that already follows every pull.
- **Update** — the same thing for one that is already there.
- **Add** — paste a URL at the bottom and name a folder inside the repository. The folder is
  suggested from the URL until you type your own.
- **Remove…** — asks first, then runs `deinit` and `git rm` with nothing forced. If the submodule
  holds work that was never committed, Git refuses and a **second** question names what forcing
  would destroy before anything happens. Its clone under `.git/modules` is kept either way, so a
  commit made in there and never pushed is never lost.

**This window commits nothing.** Adding and removing both leave their change staged, and the footer
says so and offers **Commit…**, which opens the commit window. That is the only place FlickGit
commits from.

## Commit messages by AI

Everything you need is in **Settings**, under *Commit messages (AI)*:

1. Pick who writes them — **Anthropic**, **OpenAI**, **GitHubCopilot**, or **Ollama** on your own
   machine.
2. Press **Set API key…** and paste your key. It goes into Windows Credential Manager, never
   into a settings file, and is never shown back to you. *Ollama needs no key — the button is off.*

Press **Save**. The commit window then grows a **Generate with AI** button, and writes a message
for you as soon as it opens. The pull request window uses the same provider for its description.

Choosing a provider and storing a key for it is what turns this on: from then on, the diff of the
files you are committing is sent to that provider. Choose **Disabled** and nothing is sent.

### Ollama, on your own machine

The one provider that sends nothing anywhere. Use it when policy forbids source code reaching a
third party — with the other three, the diff of what you are committing leaves the machine.

1. Install Ollama and pull a model: `ollama pull qwen2.5-coder:7b`.
2. Pick **Ollama** in Settings, and press **Save**.
3. Set the model in `%LOCALAPPDATA%\FlickGit\settings.json`:

```json
"aiModel": "qwen2.5-coder:7b"
```

**The model is required** — there is no default, because which models exist is a fact about your
disk. `ollama list` shows what you have; `flick ai` shows what FlickGit resolved.

Two things behave differently, both because the model is local:

- **The first message after a reboot is slow**, because Ollama is reading the model off disk. FlickGit
  preloads it in the background when it starts with Windows, which is what makes the rest fast, and
  it waits up to two minutes for a first token instead of the eight seconds it allows a hosted
  provider.
- **`aiOllamaUrl`** points at `http://localhost:11434` and can be pointed at another machine on your
  network, if that is where the GPU is. `flick ai` then says the diff is sent there, because at that
  point it is leaving this computer.

### Getting a GitHub token for Copilot

Copilot is the odd one out. It refuses a personal access token — `github_pat_…` or `ghp_…` — no
matter which permissions you grant it. It wants the `gho_…` OAuth token an editor signs in with.

If you have VS Code:

1. Sign out of GitHub and back in — the **Accounts** icon, bottom-left — with Copilot enabled.
2. Open `%LOCALAPPDATA%\github-copilot\apps.json`.
3. Copy the value of `oauth_token`. It starts with `gho_`.
4. Paste it into **Set API key…**.

If you do not, ask GitHub directly. Run this:

```
curl -s -X POST https://github.com/login/device/code -H "Accept: application/json" -d "client_id=Iv1.b507a08c87ecfe98" -d "scope=read:user"
```

Open [github.com/login/device](https://github.com/login/device), type in the `user_code` it
printed, and approve. Then run this, with the `device_code` from the first reply:

```
curl -s -X POST https://github.com/login/oauth/access_token -H "Accept: application/json" -d "client_id=Iv1.b507a08c87ecfe98" -d "device_code=PASTE_IT_HERE" -d "grant_type=urn:ietf:params:oauth:grant-type:device_code"
```

The `access_token` in the reply is the `gho_…` to paste into **Set API key…**.

That client id is the GitHub App VS Code's Copilot uses, which is why the token it hands back is
one Copilot accepts. If FlickGit later says GitHub would not exchange your token, it has expired
or been revoked — do the same steps again.

The same thing from a terminal:

```
flick ai              what it is configured to do
flick ai key set      store the API key (Windows Credential Manager)
flick ai key clear    remove it
```

`aiModel`, `aiMaxDiffBytes` and `aiConventionalCommits` in `settings.json` are the rest of it.
Leave `aiModel` empty and each provider uses its own default; set it to any model id that provider
accepts to override. `flick ai` prints the one actually in use.

The diff is capped before it is sent, lock files and generated code are excluded, and anything
matching a secret pattern is redacted or dropped.

The AI is never a dependency. If it is slow, unreachable or unconfigured, the message box is an
ordinary text box and every button still works.

### Changing what the AI is asked

The wording FlickGit sends is three Markdown files in `%LOCALAPPDATA%\FlickGit`, written the first
time it runs:

- **`commit-prompt.md`** — how a commit message is written.
- **`pull-request-prompt.md`** — how a pull request title and description are written.
- **`changelog-prompt.md`** — how a changelog is written (see **The log**).

Edit one and the next message uses it. There is nothing to restart, and nothing to press.

The whole file is the prompt, sent as written. HTML comments are removed first, so the notes at the
top of each file are for you rather than for the model — and so you can comment a rule out instead
of deleting it. **Delete a file to start over** — FlickGit writes it again with its built-in prompt
the next time it runs. A file with nothing left in it is refused and the built-in is used instead,
which `flick ai` will tell you.

Because FlickGit always prefers these files to its own wording, a later version that improves the
built-in prompt will not change what you see here.

While `commit-prompt.md` exists, `aiConventionalCommits` is not consulted — your file is the whole
prompt, so say there whether you want Conventional Commits.

Do not put the length in `changelog-prompt.md`. **Brief** and **Detailed** are chosen in the
changelog window itself and are sent with the commits rather than with the prompt, so a rule about
length written here fights that box instead of replacing it.

What you cannot change here is what FlickGit puts underneath: the branch, the files you are
committing, the files held back and why, and the capped, redacted diff. That half is what decides
what leaves the machine, and no prompt can widen it.

```
flick ai              names the file in use, or says built-in
flick diag doctor     the same in one line
```

## The log

**Show log…** in the FlickGit submenu, or `flick log`. A commit list, what each commit changed,
and the diff side by side.

The reason it is here: **select several commits and you get their combined diff**, not one diff
per commit. That is `git diff <oldest>^ <newest>`, so a selection with gaps in it also includes
the commits in between — and the window says so, in words, above the file list.

Nothing in this window changes the repository. There is no checkout, reset, revert or
cherry-pick here, and the diff is read-only on both sides. It has two ways of handing the range to
somebody else, and both write outside the repository.

**Save as patch…** writes the diff you are looking at to a file you choose.

**Create changelog…** writes what those commits mean for the people who *use* the software — for a
release note rather than for a reviewer. It opens on **Brief**, a flat list of one-liners; switch it
to **Detailed** for entries grouped under headings with a sentence each, and it rewrites. The text
is yours to edit before you **Copy** or **Save as…** it, and nothing is written to the repository —
there is no `CHANGELOG.md` to append to and no version number invented for you.

With no AI provider configured the window still opens, holding the commit subjects as a list. What
the AI is asked is `changelog-prompt.md`, which you can edit — see **Commit messages**.

## Pull requests

**FlickGit ▸ Pull request…**, or `flick pr`. It proposes the branch you are on, on GitHub, GitLab
or Azure DevOps — cloud or self-hosted.

The window opens already filled in: the target is your repository's trunk, the summary says what
the request would contain, and the description is written by the AI if one is configured. Pressing
**Create pull request** does three things in this order — **pushes the branch**, checks that no
request is already open for it, and creates the new one. Then it opens in your browser.

**The credential is usually one you already have.** FlickGit asks Git's own credential helper for
the token it holds for that host, so if you can `git push` you can usually open a pull request with
no setup at all. When there is nothing stored, or the service refuses it, FlickGit asks you once for
a personal access token and files it in Windows Credential Manager under the host name.

Two checkboxes: **Draft**, and **Delete the branch when it merges** — the second is hidden on
GitHub, which has no per-request setting for it.

Nothing is guessed. If the branch is diverged from its upstream, or behind it, the window says so
and creates nothing. If a request is already open, it names it and offers to open it instead.

**A self-hosted instance FlickGit cannot recognise** — `git.acme.io` could be either GitLab or
GitHub Enterprise — needs telling once, in the repository:

```
git config --local flickgit.forge gitlab
```

**A repository that proposes into `develop`** rather than into its primary branch says so the same
way:

```
git config --local flickgit.pullRequestTarget develop
```

## Blame

Right-click a **file** → **FlickGit ▸ Blame**, or `flick blame <file>`. Each line is labelled with
the commit that last touched it.

Click a line and press **Blame previous revision** to see the file as it was *before* that commit —
again and again, following the file across renames, until Git says there is nothing before it. That
is how you get past the reformat that touched every line to the change that actually wrote one.
**Back** returns, to the same line you were following.

Nothing here changes the repository. You can also reach it from the log window: right-click a file
there to blame it at the commit you are reading rather than at the working tree.

## Add and Remove a file

The other two entries on a **file**'s FlickGit menu, beside Blame.

**Add** stages the file, which for one Git has never seen is what starts tracking it. Nothing is
committed, so the file simply turns up ticked in the commit window. There is nothing to confirm and
nothing to undo — untick the row and it is out again.

**Remove…** deletes the file and stages the deletion, and asks first because it is the one that
deletes. Two things are worth knowing before you press it:

- **Git refuses if the file holds changes that were never committed**, and says which file. Nothing
  is forced, so nothing you have not committed can be lost this way.
- **HEAD still has the file**, so **Revert file…** in the commit window puts it back. What it does
  *not* do is send anything to the Recycle Bin — that is **Delete file…** in the commit window, which
  removes the file and runs no Git command at all.

An untracked file has nothing for Git to remove, and says so: Explorer's own Delete is what removes
the file itself. Both entries act on the one file you clicked, and neither appears on a folder.

## Command line

Everything in the menus is also a verb. `<path>` defaults to the current directory.

```
flick commit <path>              flick switch <path> [branch]
flick pull-rebase <path>         flick tag <path> [name]
flick push <path>                flick status <path>
flick stash <path> [message]     stash the working tree, or open the window
flick submodule <path>           add, remove, initialise submodules
flick pr <path>                  open a pull request for this branch
flick log <path>                 commit history + combined diffs
flick blame <file>               who wrote each line
flick add <file>                 stage one file
flick rm <file>                  delete one file, staged; asks first
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

Everything this window does not show lives in four files, in
`%LOCALAPPDATA%\FlickGit`:

- **`settings.json`** — hotkeys, diff font, AI provider and model, palette scan roots, the
  primary-branch override.
- **`actions.json`** — your own context-menu and palette entries, and overrides that hide,
  rename or reorder the built-in ones.
- **`commit-prompt.md`**, **`pull-request-prompt.md`** and **`changelog-prompt.md`** — what the AI
  is asked for (see **Commit messages**). Delete any of them to go back to the built-in wording.

Two more live in the repository's own `.git/config`, because they are facts about that repository
rather than about you: `flickgit.pullRequestTarget` and `flickgit.forge` (see **Pull requests**),
alongside `flickgit.primaryBranch` and `flickgit.allowUpstreamCreation`.

`settings.json` and `actions.json` are read at startup, so restart FlickGit after editing either
one; the prompt files are read on every generation and need nothing. `flick diag doctor` shows
what all of them resolved to.

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
