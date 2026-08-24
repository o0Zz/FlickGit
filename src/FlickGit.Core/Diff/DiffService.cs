using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Model;
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

    /// <summary>
    /// Pairing a change block costs O(deleted x inserted) similarity comparisons, each walking one
    /// line. Above this the block is paired positionally instead -- see <see cref="Pair"/>. A
    /// hundred deletions against a hundred insertions is a method rewritten whole, where the
    /// correspondence between one old line and one new one is not a question with an answer.
    /// </summary>
    private const int PairingCeiling = 10_000;

    /// <summary>Added to every pair's score, so that an alignment pairing more lines wins a tie.</summary>
    private const double PairBonus = 0.3;

    /// <summary>How much of a line <see cref="Similarity"/> looks at.</summary>
    private const int SimilarityCeiling = 400;

    /// <summary>The half of a row where this side has no line at all.</summary>
    private static readonly DiffSide Filler = new(null, string.Empty, []);

    private static readonly Differ LineDiffer = new();
    private static readonly SideBySideDiffBuilder Builder = new(LineDiffer);

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
        Task<FileText> rightTask = LoadWorkingCopyAsync(absolutePath, cancellationToken);

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
    /// Turns DiffPlex's line diff into aligned rows.
    ///
    /// DiffPlex is used rather than a hand-rolled Myers implementation — CLAUDE.md says so
    /// outright — but only for the <i>line</i> diff and for the word-level pass inside a pair. The
    /// alignment within a change block is this method's own, and that is the point of it.
    ///
    /// <b>Why not <c>SideBySideDiffBuilder</c>, which does the whole job.</b> It pairs a block's
    /// deleted lines with its inserted lines <i>positionally</i> — the first deletion against the
    /// first insertion, and so on until one side runs out. When the two counts differ that is the
    /// wrong correspondence, and it is visible: deleting one line while inserting three above it
    /// pairs the deleted line with the first of the three, so the red line sits beside an insertion
    /// it has nothing to do with and the line that actually replaced it lands two rows lower. The
    /// user sees a replacement whose halves are not on the same row — and the word-level
    /// highlighting inside that pair is highlighting the difference between two unrelated lines.
    ///
    /// So each block is paired by similarity instead, order-preserving, with the lines that paired
    /// with nothing emitted around the pairs. Everything outside a block is unchanged and needs no
    /// such choice.
    ///
    /// The result is what the App renders: one <see cref="DiffRow"/> per screen row on both sides,
    /// which is what makes synchronised scrolling an index copy that cannot drift.
    /// </summary>
    private static IReadOnlyList<DiffRow> BuildRows(string leftText, string rightText, bool wordLevel)
    {
        DiffResult lines = LineDiffer.CreateLineDiffs(leftText, rightText, ignoreWhitespace: false);

        string[] left = lines.PiecesOld;
        string[] right = lines.PiecesNew;

        var rows = new List<DiffRow>(Math.Max(left.Length, right.Length));
        int a = 0;
        int b = 0;

        foreach (DiffBlock block in lines.DiffBlocks)
        {
            while (a < block.DeleteStartA && b < right.Length)
                rows.Add(Unchanged(left, right, a++, b++));

            AppendBlock(
                rows,
                left, block.DeleteStartA, block.DeleteCountA,
                right, block.InsertStartB, block.InsertCountB,
                wordLevel);

            a = block.DeleteStartA + block.DeleteCountA;
            b = block.InsertStartB + block.InsertCountB;
        }

        while (a < left.Length && b < right.Length)
            rows.Add(Unchanged(left, right, a++, b++));

        return rows;
    }

    /// <summary>
    /// Emits one change block: the pairs its lines fall into, with the lines that paired with
    /// nothing around them.
    ///
    /// An insertion that comes before the pair it precedes is emitted first, so the rows come out in
    /// the order the two files have them.
    /// </summary>
    private static void AppendBlock(
        List<DiffRow> rows,
        string[] left, int leftStart, int leftCount,
        string[] right, int rightStart, int rightCount,
        bool wordLevel)
    {
        int[] pairs = Pair(left, leftStart, leftCount, right, rightStart, rightCount);
        int taken = 0;

        for (int i = 0; i < leftCount; i++)
        {
            if (pairs[i] < 0)
            {
                rows.Add(Deleted(left, leftStart + i));
                continue;
            }

            for (; taken < pairs[i]; taken++)
                rows.Add(Inserted(right, rightStart + taken));

            rows.Add(Modified(left, leftStart + i, right, rightStart + pairs[i], wordLevel));
            taken = pairs[i] + 1;
        }

        for (; taken < rightCount; taken++)
            rows.Add(Inserted(right, rightStart + taken));
    }

    /// <summary>
    /// Which insertion each deletion in a block was replaced by, as indices within the block, or
    /// −1 for a line that replaced nothing and was replaced by nothing.
    ///
    /// An order-preserving best alignment scored by <see cref="Similarity"/> — a diff of the block
    /// against itself, which is the only honest answer to "which of these three insertions is the
    /// one that replaced this deletion". A positional guess is right exactly when the two counts
    /// are equal, which is the case where it does not matter.
    /// </summary>
    private static int[] Pair(
        string[] left, int leftStart, int leftCount,
        string[] right, int rightStart, int rightCount)
    {
        var pairs = new int[leftCount];
        Array.Fill(pairs, -1);

        if (leftCount == 0 || rightCount == 0)
            return pairs;

        if ((long)leftCount * rightCount > PairingCeiling)
        {
            //Positional, which is what DiffPlex itself does. A block this large is a rewrite, and
            //in a rewrite the correspondence between one old line and one new line does not mean
            //anything -- so the cheap answer is also the honest one.
            for (int i = 0; i < Math.Min(leftCount, rightCount); i++)
                pairs[i] = i;

            return pairs;
        }

        //Each line's bigrams counted once, rather than once per candidate it is compared against.
        var leftBigrams = new Bigrams[leftCount];
        var rightBigrams = new Bigrams[rightCount];

        for (int i = 0; i < leftCount; i++)
            leftBigrams[i] = Bigrams.Of(left[leftStart + i]);

        for (int j = 0; j < rightCount; j++)
            rightBigrams[j] = Bigrams.Of(right[rightStart + j]);

        //What each candidate pair is worth, computed once so the backtrack reads the same numbers
        //the forward pass did rather than recomputing every similarity a second time.
        var score = new double[leftCount, rightCount];

        for (int i = 0; i < leftCount; i++)
        {
            for (int j = 0; j < rightCount; j++)
                score[i, j] = Similarity(leftBigrams[i], rightBigrams[j]) + PairBonus;
        }

        //best[i, j] is the highest score reachable having considered the block's first i deletions
        //and first j insertions.
        var best = new double[leftCount + 1, rightCount + 1];

        for (int i = 1; i <= leftCount; i++)
        {
            for (int j = 1; j <= rightCount; j++)
                best[i, j] = Math.Max(
                    best[i - 1, j - 1] + score[i - 1, j - 1],
                    Math.Max(best[i - 1, j], best[i, j - 1]));
        }

        int x = leftCount;
        int y = rightCount;

        while (x > 0 && y > 0)
        {
            //Never an exact equality on a double: the pairing term is one of the three values the
            //maximum was taken from, so it can only be at or below it, and a tolerance is enough to
            //recognise whether it is the one that won.
            if (best[x - 1, y - 1] + score[x - 1, y - 1] >= best[x, y] - 1e-9)
            {
                pairs[x - 1] = y - 1;
                x--;
                y--;
            }
            else if (best[x - 1, y] >= best[x, y - 1])
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        return pairs;
    }

    /// <summary>
    /// How alike two lines are, from 0 to 1: the Sørensen–Dice coefficient over their character
    /// bigrams, plus <see cref="PairBonus"/> at the call site.
    ///
    /// Bigrams rather than anything anchored to position, because the question is "is this the same
    /// line, edited" and an edit shifts everything after it — a prefix comparison answers no to a
    /// character inserted at the front.
    ///
    /// The bonus the caller adds is what breaks a tie towards pairing. Without it an alignment that
    /// pairs nothing scores the same as one pairing two unrelated lines, and the backtrack would
    /// render a plain one-for-one replacement as a deletion stacked above an insertion — the layout
    /// this whole thing exists to avoid.
    /// </summary>
    private static double Similarity(Bigrams left, Bigrams right)
    {
        if (left.Total + right.Total == 0)
            return 0;

        int shared = 0;

        foreach ((int bigram, int count) in left.Counts)
        {
            if (right.Counts.TryGetValue(bigram, out int other))
                shared += Math.Min(count, other);
        }

        return 2.0 * shared / (left.Total + right.Total);
    }

    /// <summary>
    /// One line's character bigrams, counted, packed two chars to an int.
    ///
    /// Kept beside its total because the Dice coefficient needs both, and recounting the dictionary
    /// once per comparison is the whole cost of the pairing when a block is large.
    /// </summary>
    private readonly record struct Bigrams(Dictionary<int, int> Counts, int Total)
    {
        public static Bigrams Of(string text)
        {
            //A minified bundle or an embedded blob is one line of any length, and this feeds a
            //comparison made once per candidate pair. The head of a line is what identifies it;
            //the rest is not worth the quadratic.
            int length = Math.Min(text.Length, SimilarityCeiling);

            var counts = new Dictionary<int, int>(length + 1);

            //U+0000 either end, a character no text line contains, so the first and last real
            //characters are anchored the way every other one is.
            char previous = '\0';

            for (int i = 0; i <= length; i++)
            {
                char current = i == length ? '\0' : text[i];
                int bigram = (previous << 16) | current;

                counts[bigram] = counts.TryGetValue(bigram, out int seen) ? seen + 1 : 1;
                previous = current;
            }

            return new Bigrams(counts, length + 1);
        }
    }

    private static DiffRow Unchanged(string[] left, string[] right, int a, int b) =>
        new(DiffLineKind.Unchanged,
            new DiffSide(a + 1, left[a], []),
            new DiffSide(b + 1, right[b], []));

    private static DiffRow Deleted(string[] left, int a) =>
        new(DiffLineKind.Deleted, new DiffSide(a + 1, left[a], []), Filler);

    private static DiffRow Inserted(string[] right, int b) =>
        new(DiffLineKind.Inserted, Filler, new DiffSide(b + 1, right[b], []));

    private static DiffRow Modified(string[] left, int a, string[] right, int b, bool wordLevel)
    {
        (IReadOnlyList<DiffSpan> leftSpans, IReadOnlyList<DiffSpan> rightSpans) =
            wordLevel ? WordSpans(left[a], right[b]) : ([], []);

        return new DiffRow(
            DiffLineKind.Modified,
            new DiffSide(a + 1, left[a], leftSpans),
            new DiffSide(b + 1, right[b], rightSpans));
    }

    /// <summary>
    /// The word-level ranges inside one paired line, from a side-by-side diff of that line against
    /// its counterpart.
    ///
    /// The builder is asked per pair rather than once for the whole file, because the pairs are this
    /// class's own and the builder's would be the positional ones <see cref="BuildRows"/> rejects.
    /// Two single lines produce exactly one row, whose sub-pieces are the words.
    /// </summary>
    private static (IReadOnlyList<DiffSpan> Left, IReadOnlyList<DiffSpan> Right) WordSpans(string left, string right)
    {
        SideBySideDiffModel model = Builder.BuildDiffModel(left, right, ignoreWhitespace: false);

        DiffPiece? oldPiece = model.OldText.Lines.Count > 0 ? model.OldText.Lines[0] : null;
        DiffPiece? newPiece = model.NewText.Lines.Count > 0 ? model.NewText.Lines[0] : null;

        return (
            oldPiece is null ? [] : ChangedSpans(oldPiece),
            newPiece is null ? [] : ChangedSpans(newPiece));
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
