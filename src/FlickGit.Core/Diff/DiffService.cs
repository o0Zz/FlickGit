using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Models;

namespace FlickGit.Diff;

/// <summary>
/// Produces the side-by-side diff the viewer renders.
///
/// The architecture-deciding constraint, from CLAUDE.md, "Diff Viewer": the right pane is
/// editable, so <b>`git diff` output cannot be the rendering source</b> — the moment the
/// user types a character, any hunk list Git produced is stale. This service therefore
/// diffs two in-memory buffers, and an edit re-runs exactly the same call on a debounce
/// while the user edits.
///
/// The left-hand base comes from Git; the right-hand side comes from disk:
/// <code>
/// git show HEAD:&lt;path&gt;    working tree vs HEAD
/// git show :&lt;path&gt;        working tree vs index
/// </code>
/// </summary>
public sealed class DiffService(IGitProcessRunner git, FileTextLoader files)
{
    /// <summary>Above this, keep side-by-side but drop the word-level pass. CLAUDE.md: 500 KB.</summary>
    private const long WordDiffCeilingBytes = 500 * 1024;

    /// <summary>Above either of these, fall back to a read-only unified view. CLAUDE.md: 2 MB / 50,000 lines.</summary>
    private const long SideBySideCeilingBytes = 2 * 1024 * 1024;
    private const int SideBySideCeilingLines = 50_000;

    private static readonly SideBySideDiffBuilder Builder = new(new Differ());

    public async Task<SideBySideDiff> ComputeAsync(
        RepositoryInfo repository,
        GitFileChange file,
        DiffComparisonMode mode,
        CancellationToken cancellationToken)
    {
        string absolutePath = Path.Combine(
            repository.Root,
            file.Path.Replace('/', Path.DirectorySeparatorChar));

        //Both sides fetched concurrently. The left is a process start, the right is a file
        //read; serialising them would add the whole `git show` latency to the click-to-
        //rendered budget for nothing.
        Task<FileText> leftTask = LoadBaseAsync(repository, file, mode, cancellationToken);
        Task<FileText> rightTask = LoadWorkingCopyAsync(absolutePath, file, cancellationToken);

        FileText left = await leftTask.ConfigureAwait(false);
        FileText right = await rightTask.ConfigureAwait(false);

        return await AssembleAsync(
            repository,
            file.Path,
            left,
            right,
            file.IsBinary,
            mode,
            range: null,
            UnifiedArgs(file, mode),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same diff, of two commits instead of the working tree.
    ///
    /// Both sides are blobs out of the object store, so there is nothing on disk the right pane
    /// corresponds to — which is what <see cref="SideBySideDiff.Range"/> tells the viewer, and why
    /// the result reports itself uneditable however small the file is.
    ///
    /// Not an overload of <see cref="ComputeAsync"/>: two methods differing only by whether the
    /// third argument is a mode or a range is exactly the shape Hard Requirement 1 warns about.
    /// Two different questions get two different names.
    /// </summary>
    public async Task<SideBySideDiff> ComputeRangeAsync(
        RepositoryInfo repository,
        GitFileChange file,
        CommitRange range,
        CancellationToken cancellationToken)
    {
        //Concurrently, for the reason ComputeAsync gives: two process starts serialised would add
        //a whole `git show` to the click-to-rendered budget for nothing.
        Task<FileText> leftTask = LoadBlobAsync(repository, range.BaseSpec, file.OldPath ?? file.Path, cancellationToken);
        Task<FileText> rightTask = LoadBlobAsync(repository, range.TipSpec, file.Path, cancellationToken);

        FileText left = await leftTask.ConfigureAwait(false);
        FileText right = await rightTask.ConfigureAwait(false);

        return await AssembleAsync(
            repository,
            file.Path,
            left,
            right,
            file.IsBinary,
            DiffComparisonMode.WorkingTreeVsHead,
            range,
            RangeUnifiedArgs(file, range),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything both compute paths do once they have two <see cref="FileText"/>s: the binary
    /// check, the size ceilings, the unified fallback and the row build.
    ///
    /// Extracted rather than written twice because CLAUDE.md fixes the three ceilings at 500 KB,
    /// 2 MB and 50,000 lines, and two copies of those numbers is two places for them to disagree.
    /// </summary>
    /// <param name="unifiedArgs">
    /// The fully formed argument list for the over-the-ceiling fallback, passed in rather than as
    /// a callback — the boring mechanism, and it keeps the two commands beside the two callers that
    /// know what they mean.
    /// </param>
    private async Task<SideBySideDiff> AssembleAsync(
        RepositoryInfo repository,
        string path,
        FileText left,
        FileText right,
        bool knownBinary,
        DiffComparisonMode mode,
        CommitRange? range,
        IReadOnlyList<string> unifiedArgs,
        CancellationToken cancellationToken)
    {
        if (knownBinary || left.IsBinary || right.IsBinary)
        {
            return new SideBySideDiff
            {
                Path = path,
                ComparisonMode = mode,
                Range = range,
                RenderMode = DiffRenderMode.Binary,
                Left = left,
                Right = right,
                Notice = "Binary file — no text diff.",
            };
        }

        long largest = Math.Max(left.SizeInBytes, right.SizeInBytes);
        int lines = Math.Max(left.LineCount, right.LineCount);

        if (largest > SideBySideCeilingBytes || lines > SideBySideCeilingLines)
        {
            //Read-only unified, and say so. Live re-diff at this size cannot be made to
            //feel instant, and pretending otherwise would give the user an editor that
            //stutters on every keystroke.
            GitResult unified = await git.ReadAsync(repository.Root, unifiedArgs, cancellationToken).ConfigureAwait(false);

            return new SideBySideDiff
            {
                Path = path,
                ComparisonMode = mode,
                Range = range,
                RenderMode = DiffRenderMode.UnifiedReadOnly,
                Left = left,
                Right = right,
                UnifiedText = unified.Succeeded ? unified.StdOut : unified.ErrorText,
                Notice = largest > SideBySideCeilingBytes
                    ? $"{largest / (1024 * 1024)} MB file — read-only unified view."
                    : $"{lines:N0} lines — read-only unified view.",
            };
        }

        bool wordLevel = largest <= WordDiffCeilingBytes;

        //The diff itself runs off the caller's thread. On a 2,000-line file this is a few
        //milliseconds, but the same call is re-issued on a 200 ms debounce while the user
        //types, and the UI thread must never be the one doing it.
        IReadOnlyList<DiffRow> rows = await Task.Run(
            () => BuildRows(left.Text, right.Text, wordLevel),
            cancellationToken).ConfigureAwait(false);

        return new SideBySideDiff
        {
            Path = path,
            ComparisonMode = mode,
            Range = range,
            RenderMode = wordLevel ? DiffRenderMode.SideBySideWithWordDiff : DiffRenderMode.SideBySideLinesOnly,
            Rows = rows,
            Left = left,
            Right = right,
            Notice = wordLevel ? null : "Large file — line-level diff only.",
        };
    }

    /// <summary>
    /// Re-diffs two buffers with no Git call at all. This is the live-edit path, and it
    /// is the reason the rendering source is a pair of buffers rather than `git diff`
    /// output.
    /// </summary>
    public static IReadOnlyList<DiffRow> Rediff(string leftText, string rightText, bool wordLevel) =>
        BuildRows(leftText, rightText, wordLevel);

    private async Task<FileText> LoadBaseAsync(
        RepositoryInfo repository,
        GitFileChange file,
        DiffComparisonMode mode,
        CancellationToken cancellationToken)
    {
        //An untracked file has no base by definition, and an added-in-the-index file has
        //no HEAD blob. Both are a legitimately empty left pane, not an error.
        if (file.IsUntracked)
            return FileText.Empty;

        //The old path for a rename: the base blob lives under the name it had, and asking
        //for HEAD:<newPath> would report "does not exist in HEAD" on every renamed file.
        string basePath = mode == DiffComparisonMode.WorkingTreeVsHead && file.OldPath is { Length: > 0 }
            ? file.OldPath
            : file.Path;

        string spec = mode == DiffComparisonMode.WorkingTreeVsHead
            ? $"HEAD:{basePath}"
            : $":{basePath}";

        return await LoadBlobAsync(repository, mode == DiffComparisonMode.WorkingTreeVsHead ? "HEAD" : string.Empty, basePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One blob out of the object store, as <c>git show &lt;spec&gt;:&lt;path&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A failure here is the ordinary case rather than an error, and that is what lets the range
    /// diff have no case analysis at all: a file <b>added</b> in the range has no blob in the base
    /// tree, a file <b>deleted</b> in it has none in the tip, and the empty-tree base of a root
    /// commit contains nothing at all. Every one of those is a legitimately empty pane — "the whole
    /// file was added", "the whole file was removed" — which is exactly what should be shown.
    /// </remarks>
    private async Task<FileText> LoadBlobAsync(
        RepositoryInfo repository,
        string spec,
        string path,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["show", $"{spec}:{path}"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return FileText.Empty;

        //Re-encoded to bytes so one detection path serves both sides. `git show` hands
        //back the blob's bytes, which were already decoded as UTF-8 by the process runner;
        //round-tripping keeps the size and hash fields meaningful for the left pane too.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(result.StdOut);
        return files.FromBytes(bytes);
    }

    private async Task<FileText> LoadWorkingCopyAsync(
        string absolutePath,
        GitFileChange file,
        CancellationToken cancellationToken)
    {
        //A deleted file has no working copy. The right pane is empty and the diff is the
        //whole file removed, which is exactly what should be shown.
        if (!File.Exists(absolutePath))
            return FileText.Empty;

        try
        {
            return await files.LoadAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //Locked by another process. An empty right pane with the left intact reads as
            //"everything removed", which would be a lie -- so mark it binary, which is the
            //state that suppresses the text diff entirely.
            return FileText.Empty with { IsBinary = true };
        }
    }

    private static IReadOnlyList<string> UnifiedArgs(GitFileChange file, DiffComparisonMode mode)
    {
        var args = new List<string> { "diff" };

        //Working tree against the index is plain `git diff`; against HEAD needs HEAD
        //named explicitly, or staged changes would be invisible in the output.
        if (mode == DiffComparisonMode.WorkingTreeVsHead)
            args.Add("HEAD");

        args.Add("--");
        args.Add(file.Path);

        return args;
    }

    private static IReadOnlyList<string> RangeUnifiedArgs(GitFileChange file, CommitRange range)
    {
        var args = new List<string>
        {
            "diff", "--no-color", "--no-ext-diff", "--no-textconv", "-M",
            range.BaseSpec, range.TipSpec, "--", file.Path,
        };

        //A rename needs both pathspecs or `git diff -M` has nothing to pair, and the file would
        //come back as an unrelated add.
        if (file.OldPath is { Length: > 0 } old)
            args.Add(old);

        return args;
    }

    /// <summary>
    /// Turns DiffPlex's model into aligned rows.
    ///
    /// DiffPlex is used rather than a hand-rolled Myers implementation — CLAUDE.md says so
    /// outright — but its model is not exposed beyond this method. The App renders
    /// <see cref="DiffRow"/>, so the diff library is replaceable and the renderers are
    /// testable without it.
    /// </summary>
    private static IReadOnlyList<DiffRow> BuildRows(string leftText, string rightText, bool wordLevel)
    {
        SideBySideDiffModel model = Builder.BuildDiffModel(leftText, rightText, ignoreWhitespace: false);

        //DiffPlex pads both panes to equal length with "imaginary" lines, which is the
        //alignment this whole viewer depends on: row N is row N in both editors, so
        //synchronised scrolling is index-based and cannot drift.
        int count = Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count);
        var rows = new List<DiffRow>(count);

        for (int i = 0; i < count; i++)
        {
            DiffPiece? oldPiece = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            DiffPiece? newPiece = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            DiffLineKind kind = Classify(oldPiece, newPiece);
            bool wantSpans = wordLevel && kind == DiffLineKind.Modified;

            rows.Add(new DiffRow(
                kind,
                ToSide(oldPiece, wantSpans),
                ToSide(newPiece, wantSpans)));
        }

        return rows;
    }

    private static DiffLineKind Classify(DiffPiece? left, DiffPiece? right)
    {
        ChangeType leftType = left?.Type ?? ChangeType.Imaginary;
        ChangeType rightType = right?.Type ?? ChangeType.Imaginary;

        if (leftType == ChangeType.Imaginary && rightType == ChangeType.Imaginary)
            return DiffLineKind.Filler;

        if (leftType == ChangeType.Imaginary)
            return DiffLineKind.Inserted;

        if (rightType == ChangeType.Imaginary)
            return DiffLineKind.Deleted;

        if (leftType == ChangeType.Unchanged && rightType == ChangeType.Unchanged)
            return DiffLineKind.Unchanged;

        return DiffLineKind.Modified;
    }

    private static DiffSide ToSide(DiffPiece? piece, bool wantSpans)
    {
        if (piece is null || piece.Type == ChangeType.Imaginary)
            return new DiffSide(null, string.Empty, []);

        string text = piece.Text ?? string.Empty;
        return new DiffSide(piece.Position, text, wantSpans ? ChangedSpans(piece) : []);
    }

    /// <summary>
    /// Character ranges inside a modified line, from DiffPlex's sub-pieces.
    ///
    /// The offsets have to be accumulated: sub-pieces carry text but not position, so the
    /// only way to know where a changed word sits in the rendered line is to walk them in
    /// order. Adjacent changed pieces are merged, or a renderer would draw two abutting
    /// highlights with a seam between them.
    /// </summary>
    private static IReadOnlyList<DiffSpan> ChangedSpans(DiffPiece piece)
    {
        if (piece.SubPieces.Count == 0)
            return [];

        var spans = new List<DiffSpan>();
        int offset = 0;

        foreach (DiffPiece sub in piece.SubPieces)
        {
            string text = sub.Text ?? string.Empty;

            if (sub.Type is not (ChangeType.Unchanged or ChangeType.Imaginary) && text.Length > 0)
            {
                if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == offset)
                    spans[^1] = new DiffSpan(spans[^1].Start, spans[^1].Length + text.Length);
                else
                    spans.Add(new DiffSpan(offset, text.Length));
            }

            offset += text.Length;
        }

        return spans;
    }
}
