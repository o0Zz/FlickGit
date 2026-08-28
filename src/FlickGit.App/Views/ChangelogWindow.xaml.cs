using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;
using Microsoft.Win32;

namespace FlickGit.App.Views;

/// <summary>
/// A changelog for the commits selected in the log window, written for the people who use the
/// software rather than for the people who wrote it.
///
/// <b>It is the log window's other outward action, and it is the same shape as the first.</b>
/// Save as patch hands the range to somebody who is going to apply it; this hands the same range to
/// somebody who is never going to read it. Both write outside the repository, neither runs a Git
/// command that changes anything, and both describe <i>the range</i> -- so the gap disclosure is
/// repeated here rather than left behind in the window that produced the selection.
///
/// <b>Nothing here is a Git operation.</b> The text is a draft in a box: editable, copyable,
/// savable, and gone when the window closes unless the user does one of those three things. That is
/// what lets it be regenerated on a whim, and what makes the Style box safe to press twice.
///
/// Code-behind rather than a view model, for <see cref="LogWindow"/>'s reason and one more: there is
/// no state here worth testing that is not tested in Core already -- the range is
/// <see cref="CommitRange"/>'s, the payload is <see cref="AiContextBuilder"/>'s, and what is left is
/// a text box.
/// </summary>
public partial class ChangelogWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly CommitRange _range;
    private readonly IReadOnlyList<GitFileChange> _files;
    private readonly AiTextService _ai;
    private readonly ILog _log;

    private CancellationTokenSource? _generation;

    /// <summary>True while the code is writing the box, so its own writes do not read as the user's.</summary>
    private bool _applying;

    /// <summary>
    /// The user has typed. Their words win over whatever is still arriving -- the pull-request
    /// window's rule, for its reason: a stream fighting a caret is unusable.
    /// </summary>
    private bool _edited;

    /// <summary>
    /// False until the constructor has finished filling the Style box, so populating it does not count
    /// as choosing something and start a generation before the window is on screen.
    /// </summary>
    private readonly bool _ready;

    public ChangelogWindow(
        RepositoryInfo repository,
        CommitRange range,
        IReadOnlyList<GitFileChange> files,
        AiTextService ai,
        ILog log)
    {
        InitializeComponent();

        _repository = repository;
        _range = range;
        _files = files;
        _ai = ai;
        _log = log;

        Title = Strings.Get("changelog.title", repository.Name);
        RepositoryText.Text = repository.Name;
        StyleLabel.Text = Strings.Get("changelog.style");
        WriteButton.Content = Strings.Get("changelog.write");
        CopyButton.Content = Strings.Get("changelog.copy");
        SaveButton.Content = Strings.Get("changelog.save");
        CloseButton.Content = Strings.Get("common.close");

        //The log window's own wording for the same range, rather than a second phrasing of it: two
        //windows describing one thing in two sentences is two chances to disagree about it.
        RangeText.Text = range.SelectedCount == 1
            ? Strings.Get("log.range.one", range.Newest.ShortSha)
            : range.Oldest.IsRoot
                ? Strings.Get("log.range.root", range.SelectedCount, range.Newest.ShortSha)
                : Strings.Get("log.range.many", range.SelectedCount, range.Oldest.ShortSha, range.Newest.ShortSha);

        GapText.Text = Strings.Get("log.range.gap", range.ImplicitCount);
        GapText.Visibility = range.ImplicitCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        StyleBox.Items.Add(Strings.Get("changelog.style.brief"));
        StyleBox.Items.Add(Strings.Get("changelog.style.detailed"));
        StyleBox.SelectedIndex = 0;

        //Hidden rather than disabled, which is what the commit and pull-request surfaces do with their
        //own AI buttons: a dead button invites a click that can only ever refuse.
        WriteButton.Visibility = ai.IsUsable ? Visibility.Visible : Visibility.Collapsed;

        //Something in the box before the first token arrives, and the whole of it when there is no
        //provider at all. The commits already say what happened, one line each, in the words of the
        //person who wrote them.
        Apply(Fallback());

        //Set explicitly rather than left to the TextChanged that assignment usually raises: writing an
        //empty string into an already-empty box raises nothing, so a range whose commits all have empty
        //subjects would leave both buttons live over nothing at all.
        UpdateActions();

        //"Edit it here, then copy it or save it" is what this window is for, so the caret starts in the
        //box holding the text rather than on a button. Loaded rather than here, because focus cannot be
        //given to an element that has not been arranged yet.
        Loaded += (_, _) => ChangelogBox.Focus();

        _ready = true;
    }

    /// <summary>
    /// Starts the first generation. Separate from the constructor so the caller can show the window
    /// first -- the fallback is already in the box, so there is something to read while it runs.
    /// </summary>
    public Task StartAsync() => GenerateAsync();

    /// <summary>
    /// The commit subjects, oldest first, as a bulleted list.
    ///
    /// Not a placeholder: with no AI configured this <i>is</i> the changelog, and it is a serviceable
    /// one -- which is what "the AI is an accelerator, never a dependency" requires of a window whose
    /// only content the AI writes.
    /// </summary>
    private string Fallback() =>
        string.Join(
            "\r\n",
            _range.Commits
                .Reverse()
                .Select(c => c.Subject)
                .Where(s => s.Length > 0)
                .Select(s => $"- {s}"));

    private ChangelogStyle SelectedStyle() =>
        StyleBox.SelectedIndex == 1 ? ChangelogStyle.Detailed : ChangelogStyle.Brief;

    private async void OnWrite(object sender, RoutedEventArgs e)
    {
        //An explicit press overrides "their words win", because it *is* them asking.
        _edited = false;
        await GenerateAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Changing the style rewrites, rather than applying to the next one somebody remembers to ask
    /// for. Those two words are the only control this window has, and a control that does nothing
    /// until a second button is pressed is not one.
    /// </summary>
    private async void OnStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !_ai.IsUsable)
            return;

        _edited = false;
        await GenerateAsync().ConfigureAwait(true);
    }

    private async Task GenerateAsync()
    {
        //One at a time. A second press, or a style change mid-stream, cancels the first -- otherwise
        //two streams write into one box.
        _generation?.Cancel();
        _generation?.Dispose();

        var generation = new CancellationTokenSource();
        _generation = generation;

        WriteButton.IsEnabled = false;
        StyleBox.IsEnabled = false;
        StatusText.Text = Strings.Get("changelog.writing");

        try
        {
            GenerationOutcome outcome = await _ai.StreamChangelogAsync(
                _repository,
                _range.BaseSpec,
                _range.TipSpec,
                _range.Commits,
                _files,
                SelectedStyle(),
                Apply,
                generation.Token).ConfigureAwait(true);

            //Superseded: a newer generation is already running and owns the footer and the buttons.
            //Reporting this one's outcome would overwrite its "writing…" with a stale answer.
            if (!ReferenceEquals(_generation, generation))
                return;

            if (outcome.Succeeded)
            {
                Apply(outcome.Message);
                StatusText.Text = Strings.Get("changelog.hint");
            }
            else if (outcome.FailureReason is { Length: > 0 } reason)
            {
                //Every AI failure gets the same treatment: an ordinary editable box, and one line
                //saying why. The commit subjects are still in it, so the window is still useful.
                StatusText.Text = reason;
            }
        }
        finally
        {
            if (ReferenceEquals(_generation, generation))
            {
                WriteButton.IsEnabled = true;
                StyleBox.IsEnabled = true;
                _generation = null;
            }

            generation.Dispose();
        }
    }

    private void Apply(string text)
    {
        //They started typing while it was arriving. The rest of the stream is thrown away rather than
        //fighting them for the caret.
        if (_edited)
        {
            _generation?.Cancel();
            return;
        }

        _applying = true;

        try
        {
            ChangelogBox.Text = text;

            //Follows the tail as it arrives, so a changelog longer than the box does not stream past
            //unseen.
            ChangelogBox.ScrollToEnd();
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_applying)
            _edited = true;

        UpdateActions();
    }

    /// <summary>Copy and Save mean nothing over an empty box.</summary>
    private void UpdateActions()
    {
        bool any = ChangelogBox.Text.Trim().Length > 0;

        CopyButton.IsEnabled = any;
        SaveButton.IsEnabled = any;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ChangelogBox.Text);
            StatusText.Text = Strings.Get("changelog.copied");
        }
        catch (Exception ex)
        {
            //Another process can hold the clipboard open. Worth a line in the footer and nothing more:
            //the text is still on screen and still selectable.
            _log.Debug($"Clipboard write failed: {ex.Message}");
            StatusText.Text = Strings.Get("changelog.copy.failed");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = FileName(),
            DefaultExt = ".md",
            AddExtension = true,
            Filter = Strings.Get("changelog.filter"),

            //The repository's parent, not the repository -- Save as patch's rule, for its reason: a file
            //dropped inside the working tree comes straight back as an untracked row in the commit window.
            InitialDirectory = Path.GetDirectoryName(_repository.Root.TrimEnd('\\', '/')) ?? _repository.Root,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            //UTF-8 without a BOM, and the box's own CRLF. Nothing here is round-tripping a file that
            //already existed, so there is no encoding to preserve: this is a new document, and the one
            //encoding every Markdown reader agrees about is the right one to write.
            File.WriteAllText(dialog.FileName, ChangelogBox.Text, new UTF8Encoding(false));

            StatusText.Text = Strings.Get("changelog.saved", Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice.Show(this, Strings.Get("changelog.save.failed"), $"{ex.Message}\n\n{dialog.FileName}");
        }
    }

    /// <summary>
    /// <c>changelog-4d5e6f7..a1b2c3d.md</c>, or <c>changelog-a1b2c3d.md</c> for one commit.
    ///
    /// No subject slug, unlike a patch: a patch of one commit <i>is</i> that commit, while a changelog
    /// of one commit is a rewriting of it, and naming the file after the subject would promise the two
    /// say the same thing.
    /// </summary>
    private string FileName() => _range.SelectedCount == 1
        ? $"changelog-{_range.Newest.ShortSha}.md"
        : $"changelog-{_range.Oldest.ShortSha}..{_range.Newest.ShortSha}.md";

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        //Esc closes, always, and takes the generation with it -- the commit window's rule. Cancelling
        //instead and staying open would make one key mean two things depending on timing.
        _generation?.Cancel();

        base.OnClosed(e);
    }
}
