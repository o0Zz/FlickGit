using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// A step list for a multi-stage Git operation — <c>pull --rebase</c> and its submodule update, and
/// <c>back</c>, which is a switch followed by one.
///
/// Each step is shown separately rather than as one indeterminate bar. CLAUDE.md, "Submodules": the
/// submodule update hits the network and can take noticeably longer than the pull itself, and
/// merging the two into a single spinner is what makes a working operation look hung.
///
/// <b>One button carrying two meanings, in this order.</b> While the operation runs it is Cancel and
/// Esc reaches it; once there is an outcome it becomes Close and only then may Enter reach it. A
/// default button during the run would be a killed rebase from a keystroke nobody aimed.
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly ObservableCollection<Step> _steps = [];
    private readonly CancellationTokenSource _cancellation = new();

    private readonly TextBlock _result = new()
    {
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold,
    };

    private readonly TextBox _detail = new()
    {
        Classes = { "mono" },
        IsVisible = false,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 220,
        FontSize = 11.5,
    };

    private readonly Button _close = new() { MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right };

    private bool _finished;

    public ProgressWindow(string title)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        //"Cancel" until the operation reports, then "Close" — which is what lets the one button carry
        //both Esc and Enter through the whole life of the window.
        _close.Content = Strings.Get("common.cancel");
        _close.Click += (_, _) => OnCloseRequested();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, Classes = { "title" }, TextWrapping = TextWrapping.Wrap },
                new ItemsControl { ItemsSource = _steps, ItemTemplate = StepTemplate() },
                _result,
                _detail,
                _close,
            },
        };
    }

    /// <summary>
    /// Cancels the operation the window is showing. Passed to the service instead of
    /// <see cref="CancellationToken.None"/>, which is what would leave a pull against an unreachable
    /// remote with no way to stop it.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Marks the previous step done and starts a new one. Wired to the <see cref="IProgress{T}"/> the
    /// service reports through.
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
        Finish(message, Resource("Text"), detail: null);
    }

    /// <summary>
    /// Success with a caveat: used when the pull worked but the submodules did not.
    ///
    /// Deliberately not <see cref="Fail"/>. Reporting a successful pull as a failure would invite the
    /// user to try to undo something that was fine.
    /// </summary>
    public void Warn(string message, string detail)
    {
        CompleteAll();
        Finish(message, Resource("DangerText"), detail);
    }

    public void Fail(string message, string gitError, string? suggestion)
    {
        if (_steps.Count > 0)
            _steps[^1].Fail();

        string detail = suggestion is { Length: > 0 } ? $"{gitError}\n\n{suggestion}" : gitError;

        Finish(message, Resource("DangerText"), detail);
    }

    /// <summary>
    /// The user stopped it. Reported rather than acted on: CLAUDE.md's pull section says not to abort
    /// a rebase automatically, so nothing here undoes a partly-applied one — the message says to look.
    /// </summary>
    public void Cancelled(string message)
    {
        if (_steps.Count > 0)
            _steps[^1].Fail();

        Finish(message, Resource("DangerText"), detail: null);
    }

    private void CompleteAll()
    {
        foreach (Step step in _steps)
            step.Complete();
    }

    private void Finish(string message, IBrush? brush, string? detail)
    {
        _finished = true;

        _result.Text = message;
        _result.Foreground = brush;
        _result.IsVisible = true;

        if (detail is { Length: > 0 })
        {
            _detail.Text = detail;
            _detail.IsVisible = true;
        }

        //There is an outcome on screen now, so the button stops being the way out of the operation and
        //becomes the way out of the window — which is also the moment Enter may reach it.
        _close.Content = Strings.Get("common.close");
        _close.IsDefault = true;
        _close.Focus();
    }

    /// <summary>
    /// Esc and the button both land here. While the operation runs this cancels it and the window
    /// stays to report what happened — the same exception to "Esc closes, always" that a running
    /// clone makes, and for the same reason: the outcome is the thing the user opened this for.
    /// </summary>
    private void OnCloseRequested()
    {
        if (_finished)
        {
            Close();

            return;
        }

        _cancellation.Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCloseRequested();

            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        //Closed by the title bar, or by a caller that decided not to wait. Either way the operation
        //must not outlive the only window that could report it.
        _cancellation.Cancel();
        _cancellation.Dispose();

        base.OnClosed(e);
    }

    private static FuncDataTemplate<Step> StepTemplate() =>
        new((_, _) =>
        {
            var glyph = new TextBlock { Width = 18, FontWeight = FontWeight.Bold };
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };

            glyph.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Step.Glyph)));
            glyph.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding(nameof(Step.GlyphBrush)));
            label.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Step.Label)));

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 2),
                Children = { glyph, label },
            };
        });

    private static IBrush? Resource(string key) => Application.Current?.FindResource(key) as IBrush;

    /// <summary>One row. Public only because the binding engine needs it to be.</summary>
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

        public IBrush? GlyphBrush => Resource(_brushKey);

        public void Complete()
        {
            //A step that already failed stays failed: CompleteAll runs over the whole list, and a
            //green tick over the step that stopped the operation would be a lie.
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
