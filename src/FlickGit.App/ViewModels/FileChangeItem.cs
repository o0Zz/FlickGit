using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.Models;

namespace FlickGit.App.ViewModels;

/// <summary>
/// One row of the file list.
///
/// A thin wrapper over <see cref="GitFileChange"/> rather than a copy of it:
/// <see cref="IsSelected"/> reads and writes the model's own field, so there is exactly
/// one place that knows whether a file is part of the next commit. Two copies of that flag
/// — one on the model, one on the view model — is how a tick box ends up disagreeing with
/// what gets committed.
/// </summary>
public sealed class FileChangeItem(GitFileChange change) : ObservableObject
{
    public GitFileChange Change { get; private set; } = change;

    /// <summary>
    /// Swaps in a fresh snapshot of the same path, keeping this row object identical.
    ///
    /// Refreshing a row must not replace it in the collection. The file list's selection is bound
    /// two-way, so removing the selected item makes WPF push a null selection back through the view
    /// model — which clears the open diff and, with it, the unsaved edit's context. Updating in
    /// place leaves the selection, the scroll position and the editor untouched.
    /// </summary>
    public void Update(GitFileChange change)
    {
        //The tick box is the user's, not the refresh's: a save must never untick a file.
        change.IsSelected = Change.IsSelected;

        //So is the hunk selection. A refresh that dropped it would let the commit sequence stage the
        //whole file over the top of what the user picked.
        change.HasChosenHunks = Change.HasChosenHunks;

        Change = change;

        Raise(nameof(Change));
        Raise(nameof(StatusCode));
        Raise(nameof(Added));
        Raise(nameof(Removed));
        Raise(nameof(Tooltip));
        Raise(nameof(IsUntracked));
        Raise(nameof(IsOnDisk));
        Raise(nameof(IsConflicted));
        Raise(nameof(LooksLikeSecret));
        Raise(nameof(IsDangerous));
        Raise(nameof(IsSelected));
    }

    public string Path => Change.Path;

    /// <summary>The last path segment, shown in bold; the rest is shown muted beside it.</summary>
    public string FileName
    {
        get
        {
            int slash = Change.Path.LastIndexOf('/');
            return slash < 0 ? Change.Path : Change.Path[(slash + 1)..];
        }
    }

    /// <summary>
    /// The path up to and including the final separator, so the two Runs in the row template
    /// concatenate back into the real path. Dropping the slash here renders "src b.txt" for
    /// a file that is actually at "src/b.txt" — the row template puts no separator of its own
    /// between the muted directory and the file name.
    /// </summary>
    public string Directory
    {
        get
        {
            int slash = Change.Path.LastIndexOf('/');
            return slash < 0 ? string.Empty : Change.Path[..(slash + 1)];
        }
    }

    public string StatusCode => Change.DisplayStatus.ToShortCode();

    /// <summary>
    /// The counts column. Binary files show "bin" — never "+0 -0", which would read as
    /// "nothing changed" on a file that was entirely replaced.
    /// </summary>
    public string Added =>
        Change.IsBinary ? Strings.Get("commit.summary.binary")
        : Change.AddedLines is { } added ? $"+{added}"
        : string.Empty;

    public string Removed =>
        Change.IsBinary || Change.RemovedLines is null ? string.Empty : $"-{Change.RemovedLines}";

    public bool IsSelected
    {
        get => Change.IsSelected;
        set
        {
            if (Change.IsSelected == value)
                return;

            Change.IsSelected = value;
            Raise();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Raised so the parent view model can recount and re-evaluate the Commit button.</summary>
    public event Action? SelectionChanged;

    public bool IsUntracked => Change.IsUntracked;

    /// <summary>
    /// Whether the file is still there to be deleted.
    ///
    /// Both deletion states show a <c>D</c> on the row and neither has a file left: one was removed
    /// from the working tree, the other with <c>git rm</c>. The context menu greys Delete out for
    /// both rather than offering it and then refusing.
    /// </summary>
    public bool IsOnDisk =>
        Change.WorkTreeStatus != GitChangeType.Deleted && !Change.IsDeletionStaged;

    public bool IsConflicted => Change.IsConflicted;

    public bool LooksLikeSecret => Change.LooksLikeSecret;

    /// <summary>Highlighted in the list: a conflicted file cannot be committed, a secret should not be.</summary>
    public bool IsDangerous => Change.IsConflicted || Change.LooksLikeSecret;

    /// <summary>
    /// The tooltip. Where the staged-versus-working-tree split is shown, per CLAUDE.md:
    /// "Keep the counts separate internally, display the sum, show the split in the
    /// tooltip."
    /// </summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Change.Path };

            if (Change.OldPath is { Length: > 0 })
                lines.Add(Strings.Get("files.tooltip.renamed", Change.Path, Change.OldPath));

            if (Change.IsBinary)
            {
                lines.Add(Strings.Get("files.tooltip.binary"));
            }
            else if (Change.IsUntracked)
            {
                lines.Add(Strings.Get("files.tooltip.untracked"));

                if (Change.SizeInBytes is { } size && Change.AddedLines is null)
                    lines.Add($"{size:N0} bytes");
            }
            else if (Change.StagedAddedLines is not null || Change.StagedRemovedLines is not null)
            {
                int stagedAdded = Change.StagedAddedLines ?? 0;
                int stagedRemoved = Change.StagedRemovedLines ?? 0;

                lines.Add(Strings.Get(
                    "files.tooltip.split",
                    stagedAdded,
                    stagedRemoved,
                    (Change.AddedLines ?? 0) - stagedAdded,
                    (Change.RemovedLines ?? 0) - stagedRemoved));
            }

            if (Change.IsConflicted)
                lines.Add(Strings.Get("commit.warn.conflict"));

            if (Change.LooksLikeSecret)
                lines.Add(Strings.Get("commit.warn.secret"));

            return string.Join('\n', lines);
        }
    }

    /// <summary>
    /// What a screen reader announces for this row.
    ///
    /// A `ListBoxItem` whose content is a `DataTemplate` has no text of its own, so UI Automation
    /// falls back to `ToString()` — which without this reads out the fully qualified type name for
    /// every row in the list.
    /// </summary>
    public override string ToString() =>
        $"{Change.DisplayStatus.ToShortCode()} {Path} {Added} {Removed}".TrimEnd();
}
