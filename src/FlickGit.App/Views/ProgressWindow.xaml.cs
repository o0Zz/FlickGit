using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;

namespace FlickGit.App.Views;

/// <summary>
/// A step list for a multi-stage Git operation — today `pull --rebase` and its submodule
/// update.
///
/// Each step is shown separately rather than as one indeterminate bar. CLAUDE.md,
/// "Submodules": the submodule update "hits the network and can take noticeably longer than
/// the pull itself", and merging the two into a single spinner is what makes a working
/// operation look hung.
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly ObservableCollection<Step> _steps = [];
    private readonly CancellationTokenSource _cancellation = new();

    private bool _finished;

    public ProgressWindow(string title)
    {
        InitializeComponent();

        //"Cancel" until the operation reports, then "Close" — the same swap the clone window makes,
        //and the reason the one button can carry both Esc and Enter throughout.
        CloseButton.Content = Strings.Get("common.cancel");

        Title = title;
        TitleText.Text = title;
        StepList.ItemsSource = _steps;
    }

    /// <summary>
    /// Cancels the operation the window is showing. Passed to the service instead of
    /// <see cref="CancellationToken.None"/>, which is what left a pull against an unreachable remote
    /// with no way to stop it.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Marks the previous step done and starts a new one. Wired to the
    /// <see cref="IProgress{T}"/> the service reports through.
    /// </summary>
    public void AddStep(string label)
    {
        if (_steps.Count > 0)
            _steps[^1].Complete();

        _steps.Add(new Step(label));
    }

    public void Succeed(string message)
    {
        CompleteAll();
        Finish(message, Brushes.Black, detail: null);
    }

    /// <summary>
    /// Success with a caveat: used when the pull worked but the submodules did not.
    ///
    /// Deliberately not <see cref="Fail"/>. Reporting a successful pull as a failure would
    /// invite the user to try to undo something that was fine.
    /// </summary>
    public void Warn(string message, string detail)
    {
        CompleteAll();
        Finish(message, (Brush)FindResource("DangerText"), detail);
    }

    public void Fail(string message, string gitError, string? suggestion)
    {
        if (_steps.Count > 0)
            _steps[^1].Fail();

        string detail = suggestion is { Length: > 0 } ? $"{gitError}\n\n{suggestion}" : gitError;
        Finish(message, (Brush)FindResource("DangerText"), detail);
    }

    private void CompleteAll()
    {
        foreach (Step step in _steps)
            step.Complete();
    }

    /// <summary>
    /// The user stopped it. Reported rather than acted on: CLAUDE.md's pull section says not to abort a
    /// rebase automatically, so nothing here undoes a partly-applied one -- the message says to look.
    /// </summary>
    public void Cancelled(string message)
    {
        if (_steps.Count > 0)
            _steps[^1].Fail();

        Finish(message, (Brush)FindResource("DangerText"), detail: null);
    }

    private void Finish(string message, Brush brush, string? detail)
    {
        _finished = true;

        ResultText.Text = message;
        ResultText.Foreground = brush;
        ResultText.Visibility = Visibility.Visible;

        if (detail is { Length: > 0 })
        {
            DetailText.Text = detail;
            DetailBox.Visibility = Visibility.Visible;
        }

        //There is an outcome on screen now, so the button stops being the way out of the operation and
        //becomes the way out of the window -- which is also the moment Enter may reach it. While the
        //operation was running the same button cancelled it, and Enter on that is a killed rebase from
        //a keystroke nobody aimed.
        CloseButton.Content = Strings.Get("common.close");
        CloseButton.IsDefault = true;
        CloseButton.Focus();

        SizeToContent = SizeToContent.Height;
    }

    /// <summary>
    /// Esc and the button both land here. While the operation runs this cancels it and the window
    /// stays to report what happened -- the same exception to "Esc closes, always" that a running
    /// clone makes, and for the same reason: the outcome is the thing the user opened this for.
    /// </summary>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            Close();
            return;
        }

        _cancellation.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        //Closed by the title bar, or by a caller that decided not to wait. Either way the operation must
        //not outlive the only window that could report it.
        _cancellation.Cancel();
        _cancellation.Dispose();

        base.OnClosed(e);
    }

    /// <summary>One row. Public only because WPF's binding engine needs it to be.</summary>
    public sealed class Step(string label) : ObservableObject
    {
        private string _glyph = "⟳";
        private string _brushKey = "TextMuted";

        public string Label { get; } = label;

        public string Glyph
        {
            get => _glyph;
            private set => Set(ref _glyph, value);
        }

        public Brush GlyphBrush =>
            (Brush)Application.Current.FindResource(_brushKey);

        public void Complete()
        {
            if (_glyph == "✕")
                return;

            Glyph = "✓";
            _brushKey = "Accent";
            Raise(nameof(GlyphBrush));
        }

        public void Fail()
        {
            Glyph = "✕";
            _brushKey = "DangerText";
            Raise(nameof(GlyphBrush));
        }
    }
}
