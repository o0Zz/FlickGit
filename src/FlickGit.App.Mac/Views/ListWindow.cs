using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.Models;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The shape three of these windows share: a list, a footer that reports, and a row of buttons.
///
/// <b>A base class rather than three copies</b>, and it earns that only because there are three
/// callers — Hard Requirement 2's test, not a guess. What it holds is layout and the two rules that
/// would otherwise be got subtly differently in each: Esc closes, and a reported failure carries
/// Git's own words rather than a paraphrase.
///
/// It deliberately holds no <i>behaviour</i>. Which commands a window offers, and which of them ask
/// first, is exactly the part that must be visible in each window rather than inherited.
/// </summary>
internal abstract class ListWindow : Window
{
    protected ListBox Items { get; } = new() { Margin = new Thickness(10, 0) };

    protected TextBlock Status { get; } = new()
    {
        Margin = new Thickness(10, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    protected StackPanel Buttons { get; } = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Spacing = 8,
        Margin = new Thickness(10),
    };

    protected ListWindow(string title, double width = 620, double height = 520)
    {
        Title = title;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
            Children =
            {
                Row(Items, 0),
                Row(Status, 1),
                Row(Buttons, 2),
            },
        };
    }

    /// <summary>Adds a button and returns it, so a caller can keep it to enable and disable.</summary>
    protected Button Add(string content, Func<Task> onClick, bool visible = true)
    {
        var button = new Button { Content = content, MinWidth = 120, IsVisible = visible };

        button.Click += (_, _) => _ = onClick();
        Buttons.Children.Add(button);

        return button;
    }

    /// <summary>
    /// Reports a failure in Git's own words.
    ///
    /// Never a generic sentence: CLAUDE.md requires the operation, the error and a next action, and
    /// the error is the part only Git can supply.
    /// </summary>
    protected void Report(string? gitError, string fallback) =>
        Status.Text = string.IsNullOrWhiteSpace(gitError) ? fallback : gitError;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();

            return;
        }

        base.OnKeyDown(e);
    }

    protected static T Row<T>(T control, int row)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);

        return control;
    }

    protected static T Column<T>(T control, int column)
        where T : Control
    {
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }
}
