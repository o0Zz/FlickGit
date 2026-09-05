# FlickGit — features, window by window

What the Windows UI actually offers, grouped by the window it lives in. Every entry here is
something the user can see, click or press.

---

## Ways in

| Gesture | What opens |
|---|---|
| `Ctrl+Alt+G` | Commit window on the folder Explorer is showing. No Explorer folder behind it → nothing opens. |
| `Ctrl+Alt+R` | Repository palette. |
| Explorer right-click on a folder / background / drive | *Pull (rebase)*, *Commit / Push…*, *Back to the primary branch*, then a **FlickGit ▸** submenu: Show log…, Branches…, Tags…, Submodules…, Stashes…, Push, Pull request…, Repository settings…, Clone…, Fetch (prune), Open terminal here, Add, Remove from Git. |
| Explorer right-click on a **file** | **FlickGit ▸** Blame…, Add, Remove from Git. |
| Explorer right-click on a **folder inside a repository** | Add / Remove from Git act on everything below it. |
| Tray icon (left **or** right click) | Recent repositories ▸, Settings, About, Exit. |
| `flick <verb>` from a terminal | The same code path as the menu. |

Repository folders can also carry a **badge overlay** in Explorer (opt-in: `flick install-overlay`
or the Settings checkbox). It says only *this folder is a Git repository* — never clean or modified.

---

## Conventions shared by several windows

- **Esc closes.** The one exception is the commit window while a commit is already executing.
- **F5 re-reads** in the commit window, Branches, Tags, Stashes, Submodules and Repository settings.
- **Filter box above a list** (Branches, Tags, Submodules): type to filter, `↓`/`↑` move the
  highlight while the caret stays in the box, `Enter` acts on the highlighted row. Clamped at both
  ends — it never wraps.
- **Right-click selects the row under the pointer first**, so a menu never acts on whatever happened
  to be highlighted. A right-click *inside* a multi-selection keeps the whole selection.
- **Confirmations only for what cannot be undone.** Everything else reports through a tray
  notification instead of a dialog.
- No window closes on focus loss.

---

## Commit window

`flick commit`, the context menu, `Ctrl+Alt+G`, the tray's recent list. Pre-warmed and reused.

### Header

- Repository name, and a summary line — *N changed · N untracked (excluded)*, *N selected*.
- **Branch: an editable ComboBox, top right.** Pick an existing branch, or type a name that does not
  exist to create it. A hint beside it says which of three things Enter will do before it is
  pressed: *current*, *existing — will switch*, *new — will be created*, or *not a valid branch
  name* — the only state that disables Commit.
- Ahead/behind counts against the upstream.

### Resolution bar — only during a merge, rebase, cherry-pick or revert

- Names the operation and the step (*Step 2 of 5*), and how many files are still conflicted.
- A second line spells out which side is which, and warns that a rebase reverses *ours* and
  *theirs*.
- **Continue** and **Abort `<operation>`…**. Continue refuses while any path is still unmerged.
  Abort asks in its own words, and Enter does **not** accept it.
- While an operation is in progress the Commit buttons are disabled.

### File list

- Status letter, path (directory muted, file name plain), `+added` / `−removed`, or `bin` for a
  binary file. Tooltips split staged from working-tree counts and name a rename's old path.
- **The tick box is the commit's contents.** Tracked modified and deleted files arrive ticked;
  untracked files arrive **unticked**, with a count; anything matching a secret pattern arrives
  unticked and red.
- Sorted conflicted, modified, added, deleted, renamed, untracked last.
- **All / None / Refresh** buttons underneath.
- Multi-select: click, `Ctrl+click`, `Shift+click`, `Shift+arrow`.
- **`Del`** — Remove from Git (`git rm --cached`; the file stays on disk) for any row the index
  holds, and the Recycle Bin for an untracked row. Neither half asks. Only the bare key —
  `Shift+Del` deliberately does nothing.

### File-list right-click

Item labels count what would actually be touched, so a five-row selection can read *Revert 4
files…*.

| Item | Behaviour |
|---|---|
| **Use ours** / **Use theirs** | Only when the selection holds a conflict, and only for the sides that exist. |
| **Mark resolved** | Stages the file as resolved. |
| **Edit** | Opens the file with `externalEditor` from `settings.json`, or Notepad. |
| **Add** | Stages. On a staged-deletion row whose file is still on disk this is the exact inverse of `Del`. |
| **Revert file…** | Puts the row back the way HEAD has it. **The one thing in this window that discards uncommitted work** — the copy on disk goes to the Recycle Bin first, and it asks. A staged addition instead simply leaves the index: nothing is binned and the row is unticked. Untracked, renamed and conflicted rows are skipped. |
| **Delete file** / **Remove from Git** | Two spellings of one item, chosen by whether Git holds the row. Never asks. |

### Commit message

- The caret is in the message box from the moment the window is populated.
- Loads `MERGE_MSG` when Git prepared one.
- **Generate with AI** — shown only when a provider and key are configured. The message streams in.

### Footer

- **Commit & Push** (default), **Commit**, **Close**.
- The status line shows the outcome (*Committed a1b2c3d subject*); until there is one it shows the
  keyboard map.

### Keyboard

```
⏎          commit & push               ⇧⏎    newline in the message
Ctrl+⏎     commit & push from anywhere, including the diff pane
Ctrl+S     save the diff pane's edit    F5    re-read status
Ctrl+F     find in the diff pane        F3 / ⇧F3   next / previous match
Del        file list only — remove from Git, or bin an untracked file
esc        close the search bar if open, otherwise the window
```

- Enter is suspended while the **diff pane** has keyboard focus — there it is a newline in the
  user's file.
- Enter pressed before an AI message arrives **queues** the commit: the button reads *Committing
  when the message arrives…* and it fires the instant the text lands. A failed generation cancels
  the queue and puts the caret back in the message box.
- Closing with an unsaved diff-pane edit asks Save / Discard / Keep editing.

---

## Diff pane

The right-hand half of the commit window, and the lower half of the Log and Stashes windows.

- **Side by side, aligned row for row.** Left is the base — HEAD in the commit window, a revision
  elsewhere; right is the working tree.
- **The right pane is editable** in the commit window: a live editor over the file on disk.
  Read-only for binary files, oversized files, and anywhere a revision range is shown. A
  **conflicted file is deliberately editable** — taking the markers out and pressing *Mark resolved*
  is one of the two ways out.
- The header names the comparison. A **staged-file strip** appears when you edit a file that is
  already staged, with a one-click **Restage**.
- Opens on the first change with three lines of context above, not on line 1.
- Synchronised scrolling, and a draggable 4 px splitter between the panes.
- **Overview strip** down the right edge — the whole file's changes as green, red and blue marks.
  Decorative; the scrollbar beside it is what you drag.
- **Buttons:** Revert lines · Stage hunk · Unstage hunk · Save. A disabled button carries a tooltip
  saying why.
- **Right-click, either pane:** Revert lines · Stage hunk · Unstage hunk. Acts on the selection when
  the click was inside it, otherwise on the line under the pointer.
- **Ctrl+F** find bar: match counter, ▲ ▼, which pane the match is in, `F3` / `Shift+F3`, `Esc` to
  close.
- **Ctrl+S** saves. **It never auto-saves.** Encoding, BOM, line endings and trailing newline are
  preserved exactly. If the file changed on disk since it was loaded: Overwrite / Reload from disk /
  Keep editing, defaulting to the non-destructive answer.
- **Ctrl+Z** undoes, including a *Revert lines* — that is an editor edit, not a Git operation.

---

## Branches (`flick switch`)

- Filter box, then local branches with remote-tracking ones below, and a **create row** when what
  you typed matches no ref. Creating validates with `check-ref-format` and branches from HEAD.
- **Enter**, **double-click** or the **Switch** button switches. `Esc` closes.
- **A refused switch** opens a panel listing the blocking files, offering
  **[ Stash, switch, restore ]** — which restores only the stash it created — or Close. Never
  automatic, never forced.
- **Worktrees live on the rows.** A branch checked out in another worktree turns the primary button
  into **Open folder**, since a switch is the one thing Git would refuse there.
- **Right-click a row:**
  - *Create worktree…* — a folder picker; refuses a path inside the repository, a non-empty folder
    or a relative path
  - *Open folder* · *Remove worktree…* · *Clean up missing worktrees…* on a row that has one
  - *Delete branch…* on a local row — `branch -d` first, and an unmerged refusal gets its own second
    question before `-D`
  - *Delete on `<remote>`…* on a remote row — the one thing in FlickGit that destroys state other
    people share, confirmed in its own words
- No menu at all on the current branch or on the create row.

---

## Tags (`flick tag`)

- Filter box over the tag list. Annotated tags show their subject; the rest read *lightweight*.
- **New tag** panel: **Name** plus an optional **Message** — anything typed there makes it an
  annotated tag. `Enter` in either box creates it. A live hint says exactly what will happen
  (*created on HEAD and pushed to origin*, or *there is no remote to publish it to*), and refuses an
  invalid name or one that already exists — **FlickGit never moves a tag**; delete it first.
- **Double-click**, or right-click **Check out `<tag>`…**, after one question. **This is the only
  thing in FlickGit that detaches HEAD**, and the window stays open to say so.
- Right-click **Delete tag…** / **Delete tag, here and on `<remote>`…** — remote first, never
  forced, and the confirmation says a tag has no reflog.

---

## Stashes (`flick stash`)

- **The list**, and under it a file list with a read-only diff pane showing **what is in the stash
  you are pointing at** — including its untracked half, which `git stash show` omits. Nothing in
  that half writes anything.
- **Stash your changes** panel: an optional message, an **Include untracked** checkbox (ticked by
  default; ignored files are never included either way), and the **Stash** button. `Enter` in the
  message box stashes.
- **Double-click**, or right-click **Pop `stash@{n}`** — asks nothing, and a failed pop always
  leaves the stash in place.
- Right-click **Drop `stash@{n}`…** asks, because a stash has no reflog. A multi-selection gives
  **Drop N stashes…**: one question with the totals, dropped highest index first, and a batch that
  stops half-way reports how many went.
- Every pop and drop re-reads the list and **refuses if the row no longer names the same stash** —
  positions move whenever anything else pushes or pops.
- Pop takes one row only; a double-click inside a multi-selection says so in the footer.

---

## Submodules (`flick submodule`)

- Filter box over every declared submodule, initialised or not, with its state (*not initialised*,
  *changed*).
- **Right-click:** *Initialise* on an uninitialised row · *Update* · *Open folder* · *Remove…*
- **Double-click** opens the folder.
- **Add a submodule** panel: a repository URL and a target folder inside the repository. Refuses
  before Git runs — no URL, no path, an absolute or escaping path, a non-empty folder, or a path
  already declared.
- **Nothing here commits.** Both operations leave their work staged, and the **Commit…** button
  opens the commit window. Removing keeps `.git/modules/<name>`, and a submodule holding
  uncommitted work needs a second, explicit answer.

---

## Log (`flick log`)

- Newest-first commit list: short sha, subject, `HEAD` / branch / tag decorations, author and date.
  **Load N more** at the bottom, and the footer says when the whole history is loaded.
- **Multi-select for a combined diff** — click, `Ctrl+click`, `Shift+click`, `Shift+arrow`. The
  range line names it (*3 commits · 4d5e6f7^..a1b2c3d*) and **discloses the gap**: *including 1 you
  did not select*.
- One commit selected shows its subject, body, author, date and parents; a merge says the diff is
  against its first parent.
- **File list and diff pane** for the range, with totals (*12 files · +418 −233*).
- **Right-click a file → Blame at this commit…**
- **Create changelog…** opens the changelog window on the same range.
- **Save as patch…**, also `Ctrl+S`, writes `git diff --binary` straight to a file.
- **Nothing in this window writes to the repository** — no checkout, reset, revert, cherry-pick,
  amend, tag or branch-from-here.

---

## Changelog

Opened from the Log window.

- Repeats the range and the gap disclosure.
- **Style:** Brief or Detailed. **Write again** re-runs it.
- The text is **editable in place**. With no AI configured it opens holding the commit subjects as a
  bulleted list.
- **Copy** to the clipboard, **Save as…** to a `.md` file. Nothing is written to the repository — no
  version number, no date, no `CHANGELOG.md`.

---

## Blame (`flick blame <file>`)

- One read-only editor with a **blame gutter**, drawn once per run of the same commit. Click the
  gutter to select a line.
- The panel below names the commit, author, date and line number for the caret's line.
- **Blame previous revision (`sha`)** — the reason this window exists. It walks back through Git's
  own `previous` record, following renames. Also on **double-click** in the editor.
- **← Back**, or **`Alt+←`**, returns and restores the caret line as well as the revision. The
  header shows how far back you are (*3 back*).
- Says *This is the first commit that touched the file* rather than guessing, and refuses a binary
  file outright.

---

## Pull request (`flick pr`)

- Header: the source branch, an editable **target branch** ComboBox over the branches that exist on
  the remote, and the forge. Nothing touches the network before the window paints.
- Summary: *N commits · N files · +N −N*, read against the merge base.
- **Title** and **Description** boxes, and **Write with AI**, which fills both from one request —
  the first line becomes the title.
- **Draft** checkbox, and **Delete `<branch>` when it merges**.
- **An existing pull request for this branch** is found before creating and shown in a strip with an
  **Open** button — never a duplicate.
- **Create pull request** (`⏎` / `Ctrl+⏎`) runs push → credential → duplicate check → create → open
  in the browser, reporting each step. A diverged branch is refused here exactly as from the commit
  window, and force-push is offered nowhere.
- A missing credential is asked for once and stored per host.

---

## Repository settings (`flick repo`)

- **Identity** — use the global identity (shown inline), or set a name and email for this
  repository.
- **Remotes** — the list with *tracked* and *push:* annotations, plus **Add**, **Save remote** (a
  rename and a re-point in one press run rename first) and **Remove**, which asks. These apply per
  button, immediately.
- **FlickGit, for this repository** — `flickgit.primaryBranch`, and the remembered answer to "may a
  new branch create an upstream here", with an **Ask again** button.
- **Save** applies the identity and the defaults. `Esc` closes. No network and no global config.

---

## Clone

Shown when the folder you clicked is not inside a repository.

- **URL** — prefilled from the clipboard only when it looks like a Git remote.
- **Into** — always a subdirectory of the clicked folder.
- **Initialise submodules** and **Shallow clone (--depth 1)** checkboxes.
- A determinate **progress bar** parsed from `git --progress`, with the phase, a percentage and a
  scrolling log.
- **Cancel** kills the process tree and deletes the partial directory. The button becomes **Close**
  once the clone finishes.

---

## Pull (rebase), and Back to the primary branch

A small step-list window — one row per stage rather than a single bar, because the submodule update
hits the network separately and would make one bar look stalled.

- **Pull (rebase)** — `pull --rebase --autostash`, then `submodule update --init --recursive` as a
  distinct step when `.gitmodules` exists. A submodule failure does not roll back the pull. A
  conflict says so and **does not abort the rebase**.
- **Back to the primary branch** — resolve the primary branch, switch (or not, if you are already
  on it), then pull. A refused switch offers stash-switch-restore, and if the restore conflicts the
  pull does not run. An operation in progress is refused by name. A detached HEAD is left rather
  than refused, and the outcome names the commit you left so you can get back to it.
- **Cancel** stops the operation; the button becomes **Close** when it reports.

---

## Repository palette (`Ctrl+Alt+R`, `flick palette`)

- Opens **instantly from cache**, on repositories that have **something to do**, then refreshes in
  place. A bullet marks the ones with work; each row shows *N modified* / *N untracked* / *clean*
  and an ahead/behind marker.
- **Type to filter** — subsequence fuzzy matching (`cnb` → `commit-new-branch`), scored by
  contiguity, word boundaries and MRU rank.
- **Space** or **`>`** after a repository switches to **action mode**: a second token picks the
  action and completes from the catalog, and a branch action completes from that repository's refs.
- **The footer shows the literal command Enter would run**, and marks an action that *asks first*.
- Keys: `⏎` run · `⇥` complete · `Ctrl+⏎` pull --rebase every repository that is behind · `↑` / `↓`
  move · `Backspace` at the start leaves action mode · `Esc` dismisses with no side effects.
- The empty state always names a next step.

---

## Settings (`flick settings`)

Three tabs. Every value is read from its source of truth on open — the registry, the Task Scheduler
— never from a remembered flag. **Nothing applies until Save**, except the API key.

**Settings tab**

- *Explorer* — **Show FlickGit in the Explorer context menu**; **Badge repository folders in
  Explorer** (needs administrator once, and takes effect after Explorer restarts); **Start FlickGit
  at login**.
- *Commit* — **Close the commit window after a successful commit**; **Show a notification after a
  successful commit**.
- *Editor* — the program the commit window's **Edit** item uses, with **Browse…**. Empty means
  Notepad.
- *Pull* — **Close the pull window after a successful pull**.
- *Commit messages (AI)* — the provider (Disabled, Anthropic, OpenAI, Copilot, Ollama), **Set API
  key…** and **Remove key**, and a line saying whether a key is stored that states outright that the
  diff of the files you commit is sent to that provider. Ollama says it needs no key and points at
  `ollama list`.
- *Language* — the languages built into this copy, or Automatic. Says to restart.
- **Open configuration folder**, for `settings.json`, `actions.json`, the three prompt files, icons
  and logs.

**Help tab** — renders `Help.md` read-only.

**About tab** — the icon, version, tagline, author and a link to the project.

---

## Dialogs

- **Confirm** — one question and up to three buttons. A destructive affirmative is coloured, the
  default button and the accent always agree, and Esc always means *nothing happened*. Where both of
  the first two answers would lose something, Enter sits on the third (*Keep editing*) rather than
  on either.
- **Notice** — an error: the operation, Git's own message, the repository path and a next action.
  Never "Something went wrong."
- **API key / forge token** — a password box, a line saying the value is stored in Windows
  Credential Manager and never printed back, and the credential target it will be filed under.

---

## Tray icon

A left **or** right click opens the menu, placed at the pointer.

- **Recent repositories ▸** — folder name and full path, rebuilt each time the menu opens; opens the
  commit window on one of them.
- **Settings**, **About**, **Exit**.
- Successful outcomes arrive here as **notifications** rather than dialogs — *Staged 5 items*,
  *Removed 3 items*, the commit toast. Three consecutive AI failures raise a persistent warning.
