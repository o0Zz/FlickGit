using FlickGit.Diff;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace FlickGit.App.Rendering;

/// <summary>
/// The editable pane's document, and the one thing that knows it is <b>not</b> the file.
///
/// The two diff panes are kept in step by giving each a document with one line per diff row, which
/// means the editable one contains blank <i>filler</i> lines that exist only to hold the alignment.
/// Saving that document verbatim would write blank lines into the user's source.
///
/// So every conversion between "what the editor holds" and "what the file is" happens here, and
/// nowhere else:
///
/// <list type="bullet">
/// <item><description><see cref="ToFileText"/> — the only value that may ever be written to
/// disk.</description></item>
/// <item><description><see cref="CaretFileOffset"/> and <see cref="RestoreCaret"/> — so a rebuild
/// puts the caret back where the user left it, even though the filler layout either side of the
/// rebuild is different.</description></item>
/// </list>
///
/// Filler positions are tracked with AvalonEdit <see cref="TextAnchor"/>s rather than line numbers.
/// That is load-bearing: a list of numbers captured when the document was built goes stale the
/// moment the user inserts or removes a line, and the reconstruction would then drop the wrong
/// ones — silently writing a blank line into the file, or silently deleting one the user typed.
/// An anchor moves with its text.
/// </summary>
public sealed class AlignedDocument(TextEditor editor)
{
    private readonly List<TextAnchor> _fillerAnchors = [];

    /// <summary>
    /// Replaces the document with the two panes' text and re-anchors the fillers.
    ///
    /// The caller is responsible for suppressing its own change handling around this: assigning
    /// <c>Text</c> raises <c>TextChanged</c>, which would otherwise look like the user typing.
    /// </summary>
    public void Load(string text, IReadOnlyList<int> fillerLines)
    {
        editor.Text = text;

        //Anchors are created after the text, because they are positions in it.
        _fillerAnchors.Clear();

        foreach (int line in fillerLines)
        {
            if (line > editor.Document.LineCount)
                continue;

            TextAnchor anchor = editor.Document.CreateAnchor(editor.Document.GetLineByNumber(line).Offset);

            //AfterInsertion so that typing on a filler line leaves the anchor on that same line
            //rather than being pushed onto the next one.
            anchor.MovementType = AnchorMovementType.AfterInsertion;

            //Survives deletion of its line, so IsDeleted can be asked rather than throwing.
            anchor.SurviveDeletion = true;

            _fillerAnchors.Add(anchor);
        }
    }

    /// <summary>Forgets the fillers, for a document that is no longer an aligned diff.</summary>
    public void Clear() => _fillerAnchors.Clear();

    /// <summary>
    /// The file's text as the editor now holds it, with the filler lines removed.
    ///
    /// <b>The only value that may ever be written to disk.</b>
    /// </summary>
    public string ToFileText(bool endsWithNewline) =>
        DiffDocument.ToFileText(Lines(), CurrentFillerLines(), endsWithNewline);

    /// <summary>Where the caret is, counted in the file rather than in the document.</summary>
    public int CaretFileOffset => ToFileOffset(editor.CaretOffset);

    /// <summary>Puts the caret back, against whatever filler layout the document has now.</summary>
    public void RestoreCaret(int fileOffset) => editor.CaretOffset = ToDocumentOffset(fileOffset);

    /// <summary>Every line of the document, without terminators.</summary>
    private List<string> Lines()
    {
        int count = editor.Document.LineCount;
        var lines = new List<string>(count);

        for (int line = 1; line <= count; line++)
            lines.Add(editor.Document.GetText(editor.Document.GetLineByNumber(line)));

        return lines;
    }

    /// <summary>
    /// The filler line numbers as they stand now, resolved from the anchors.
    ///
    /// An anchor whose line was deleted reports <c>IsDeleted</c> and is skipped: the padding it
    /// marked is gone.
    /// </summary>
    private HashSet<int> CurrentFillerLines()
    {
        var lines = new HashSet<int>();

        foreach (TextAnchor anchor in _fillerAnchors)
        {
            if (!anchor.IsDeleted)
                lines.Add(anchor.Line);
        }

        return lines;
    }

    private int ToFileOffset(int documentOffset)
    {
        HashSet<int> fillers = CurrentFillerLines();
        int file = 0;
        int count = editor.Document.LineCount;

        for (int line = 1; line <= count; line++)
        {
            DocumentLine documentLine = editor.Document.GetLineByNumber(line);
            string text = editor.Document.GetText(documentLine);
            bool skipped = text.Length == 0 && fillers.Contains(line);

            if (documentOffset <= documentLine.EndOffset)
                return skipped ? file : file + (documentOffset - documentLine.Offset);

            if (!skipped)
                file += text.Length + 1;
        }

        return file;
    }

    private int ToDocumentOffset(int fileOffset)
    {
        HashSet<int> fillers = CurrentFillerLines();
        int file = 0;
        int count = editor.Document.LineCount;

        for (int line = 1; line <= count; line++)
        {
            DocumentLine documentLine = editor.Document.GetLineByNumber(line);
            string text = editor.Document.GetText(documentLine);

            if (text.Length == 0 && fillers.Contains(line))
                continue;

            if (fileOffset <= file + text.Length)
                return documentLine.Offset + (fileOffset - file);

            file += text.Length + 1;
        }

        return editor.Document.TextLength;
    }
}
