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

    public ProgressWindow(string title)
    {
        InitializeComponent();

        CloseButton.Content = Strings.Get("common.close");

        Title = title;
        TitleText.Text = title;
        StepList.ItemsSource = _steps;
    }

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

    private void Finish(string message, Brush brush, string? detail)
    {
        ResultText.Text = message;
        ResultText.Foreground = brush;
        ResultText.Visibility = Visibility.Visible;

        if (detail is { Length: > 0 })
        {
            DetailText.Text = detail;
            DetailBox.Visibility = Visibility.Visible;
        }

        CloseButton.IsEnabled = true;
        CloseButton.Focus();

        SizeToContent = SizeToContent.Height;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
