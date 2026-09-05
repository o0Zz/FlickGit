//
//  The Finder Sync extension: the FlickGit context menu, and the repository badge.
//
//  This is the macOS answer to FlickGit.Shell.dll, and it is a different thing rather than a port.
//  None of the COM identity, the hand-rolled vtables, the PIDL parsing or the HBITMAP menu icons has
//  an analogue here; what carries over is the *rules*, which are the part that mattered:
//
//    - No Git logic. The whole of what this knows about Git is whether a directory contains a
//      `.git` entry, which is one stat call. Everything else is `flick`'s job.
//    - Nothing blocking. Finder calls into this on the thread drawing the view, so the badge test
//      is a single file-system probe with an early exit and no cache to invalidate.
//    - Every action is a launch. The extension starts `flick` and returns; it never waits for it.
//
//  Swift because it must be: a Finder Sync extension is a sandboxed macOS app extension, so it
//  cannot host .NET and cannot be written in C#.
//

import Cocoa
import FinderSync

class FinderSync: FIFinderSync {

    /// The badge shown on a repository root. One badge, saying "this folder is a Git repository" and
    /// nothing else — not clean, not modified, not ahead of the remote, and not on the folders
    /// inside it. Every rule below follows from that: with no status to compute there is no
    /// `git.exe`, no socket, no cache and nothing to invalidate.
    private static let repositoryBadge = "flickgit.repository"

    /// Where `flick` is, inside the same app bundle this extension ships in.
    ///
    /// Derived from the extension's own bundle rather than looked up on PATH: an app extension's
    /// environment is not the user's shell, so PATH is not something to rely on, and the executable
    /// that belongs to *this* build is the one that should be run.
    private lazy var flickPath: String = {
        // .../FlickGit.app/Contents/PlugIns/FlickGitFinder.appex/Contents/MacOS/…
        // → .../FlickGit.app/Contents/MacOS/flick
        let appex = Bundle.main.bundleURL
        let app = appex.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()

        return app.appendingPathComponent("MacOS/flick").path
    }()

    override init() {
        super.init()

        // The directories this extension is asked about. Finder only calls back for items inside
        // them, and repositories live wherever the user keeps them — so it is the home directory and
        // every mounted volume rather than a configured list nobody would remember to update.
        var roots = [URL(fileURLWithPath: NSHomeDirectory())]

        if let volumes = try? FileManager.default.contentsOfDirectory(
            at: URL(fileURLWithPath: "/Volumes"),
            includingPropertiesForKeys: nil) {
            roots.append(contentsOf: volumes)
        }

        FIFinderSyncController.default().directoryURLs = Set(roots)

        if let badge = NSImage(named: NSImage.statusAvailableName) {
            FIFinderSyncController.default().setBadgeImage(
                badge,
                label: "Git repository",
                forBadgeIdentifier: FinderSync.repositoryBadge)
        }
    }

    // MARK: - Badges

    /// Called once per drawn item, on the thread painting the view.
    ///
    /// The hottest callback in the product, and the tests are in cost order with an early exit each:
    /// a plain *file* costs one attribute lookup, and only a directory reaches the probe. There is
    /// deliberately **no cache** — every call is a different path, so one would never hit.
    override func requestBadgeIdentifier(for url: URL) {
        guard isDirectory(url) else { return }
        guard hasGitEntry(url) else { return }

        FIFinderSyncController.default().setBadgeIdentifier(
            FinderSync.repositoryBadge,
            for: url)
    }

    private func isDirectory(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true
    }

    /// Whether the directory holds a `.git` entry.
    ///
    /// A file as well as a directory counts: a worktree and a submodule both have `.git` as a file
    /// containing a `gitdir:` line, and treating those as "not a repository" would leave the badge
    /// off exactly the folders a worktree user works in.
    private func hasGitEntry(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.appendingPathComponent(".git").path)
    }

    // MARK: - Menu

    override var toolbarItemName: String { "FlickGit" }
    override var toolbarItemToolTip: String { "FlickGit" }
    override var toolbarItemImage: NSImage { NSImage(named: NSImage.folderName)! }

    /// The menu, mirroring the Windows projection: the three entries the user *performs* all day at
    /// the top, everything else one level down. Windows 11 allows only one level of submenu and so
    /// does this, which is why the two agree without either being contorted.
    ///
    /// **A file gets a different menu from a folder**, exactly as `ActionSurfaces` decides on
    /// Windows: Blame, Add and Remove from Git are what apply to one file, and none of the
    /// repository-wide entries is drawn beside them. Add and Remove are absent from a folder
    /// *background* for the same reason they are there — the root of a repository is where Commit
    /// already stages everything, and `flick add .` from a terminal is refused by name.
    ///
    /// **Fetch (prune) is deliberately not here.** It is a user action from `actions.json`, and the
    /// Windows menu only carries it because `install-shell` writes a registry projection of the
    /// catalog. There is no equivalent for a Finder Sync extension to write, so this menu is the
    /// built-ins and nothing else — see `docs/macos-port.md`, §4a.
    override func menu(for menuKind: FIMenuKind) -> NSMenu {
        let menu = NSMenu(title: "FlickGit")

        guard let target = self.target() else { return menu }

        let repository = hasGitEntry(target) || isInsideRepository(target)

        guard repository else {
            // Not a repository: clone is the offer, and it is the default rather than `git init`.
            add(menu, "Clone here…", "clone")

            return menu
        }

        // One clicked file. Everything else on this menu is about a repository, and none of it is
        // what the user meant by right-clicking a source file.
        if menuKind == .contextualMenuForItems, !isDirectory(target) {
            let one = NSMenu(title: "FlickGit")
            add(one, "Blame…", "blame")
            add(one, "Add", "add")
            add(one, "Remove from Git", "rm")

            let item = NSMenuItem(title: "FlickGit", action: nil, keyEquivalent: "")
            item.submenu = one
            menu.addItem(item)

            return menu
        }

        // The three root entries, because those are the three the user performs all day — and the
        // third is the one that gets them out of the second and back to the first.
        add(menu, "Pull (rebase)", "pull-rebase")
        add(menu, "Commit / Push…", "commit")
        add(menu, "Back to the primary branch", "back")

        let more = NSMenu(title: "FlickGit")
        add(more, "Show log…", "log")
        add(more, "Branches…", "switch")
        add(more, "Tags…", "tag")
        add(more, "Stashes…", "stash")
        add(more, "Submodules…", "submodule")
        add(more, "Push", "push")
        add(more, "Pull request…", "pr")
        add(more, "Repository settings…", "repo")
        add(more, "Clone…", "clone")
        add(more, "Open terminal here", "terminal")

        // Only on a folder the user pointed at, never on the background of one: on a folder these
        // act on everything below it, and on a repository root that is what Commit already is.
        if menuKind == .contextualMenuForItems {
            add(more, "Add", "add")
            add(more, "Remove from Git", "rm")
        }

        let item = NSMenuItem(title: "FlickGit", action: nil, keyEquivalent: "")
        item.submenu = more
        menu.addItem(item)

        return menu
    }

    /// Whether the clicked item is a folder.
    ///
    /// One `stat`, on Finder's own thread, and only when a menu is being built — never on the badge
    /// path, which is the one with a per-drawn-item budget.
    private func isDirectory(_ url: URL) -> Bool {
        var directory: ObjCBool = false

        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &directory) else {
            return false
        }

        return directory.boolValue
    }

    /// The clicked folder, or the folder being shown.
    private func target() -> URL? {
        let controller = FIFinderSyncController.default()

        if let selected = controller.selectedItemURLs()?.first {
            return selected
        }

        return controller.targetedURL()
    }

    /// Whether any ancestor is a repository, so the menu is right inside a subdirectory too.
    ///
    /// Bounded at sixteen levels, which is the same bound the Windows handler uses: a walk with no
    /// limit is a walk that can be made expensive by a deep path, on Finder's own thread.
    private func isInsideRepository(_ url: URL) -> Bool {
        var current = url
        var levels = 0

        while levels < 16, current.path != "/" {
            if hasGitEntry(current) { return true }

            current = current.deletingLastPathComponent()
            levels += 1
        }

        return false
    }

    private func add(_ menu: NSMenu, _ title: String, _ verb: String) {
        let item = NSMenuItem(title: title, action: #selector(run(_:)), keyEquivalent: "")

        item.target = self
        item.representedObject = verb

        menu.addItem(item)
    }

    /// Launches `flick` and returns.
    ///
    /// Never waits: this runs on Finder's thread, and a menu action that blocked would freeze the
    /// window it was clicked in. `flick` itself decides whether to answer in text, forward to the
    /// resident service, or open a window — one route, the same as every other surface.
    @objc private func run(_ sender: AnyObject?) {
        guard let verb = (sender as? NSMenuItem)?.representedObject as? String else { return }

        let paths = FIFinderSyncController.default().selectedItemURLs()?.map { $0.path }
            ?? [FIFinderSyncController.default().targetedURL()?.path].compactMap { $0 }

        guard !paths.isEmpty else { return }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: flickPath)

        // add and rm are the only two verbs that take more than one path; everything else is handed
        // the item under the pointer, because the token after its path means a branch or a tag name.
        process.arguments = (verb == "add" || verb == "rm") ? [verb] + paths : [verb, paths[0]]

        do {
            try process.run()
        } catch {
            NSLog("FlickGit: could not launch %@: %@", flickPath, error.localizedDescription)
        }
    }
}
