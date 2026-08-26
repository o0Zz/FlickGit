using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace FlickGit.App.Rendering;

/// <summary>
/// Turns a Markdown file into a WPF <see cref="FlowDocument"/>.
///
/// The Help tab shows a file rather than a XAML page precisely so it can be edited without a
/// build, and a file already written in Markdown shown as plain text would waste the format it is
/// written in. Hence a renderer -- but a small one: headings, paragraphs, lists, quotes, rules,
/// fenced code, and the four inline forms (<c>**bold**</c>, <c>*italic*</c>, <c>`code`</c>,
/// <c>[text](url)</c>). Anything else shows as the literal text it is.
///
/// <b>Not a Markdown library.</b> CLAUDE.md fixes the third-party list at three packages, and a
/// dependency for one tab rendering one file we ship ourselves is not the trade this product makes.
/// A construct this does not understand degrades to its own source text, which for a help file is a
/// legible failure rather than a broken one.
///
/// A pure function of its argument, which is one of the three kinds of static Hard Requirement 3
/// keeps. The exception is the click handler on a link: opening a URL is the shell's job and there
/// is nothing here to substitute for it.
/// </summary>
public static class MarkdownFlow
{
    private const string Monospace = "Cascadia Mono, Consolas, Courier New";

    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            PagePadding = new Thickness(2, 0, 14, 8),
            LineHeight = 18,
            Foreground = Brush("Text", Brushes.Black),
        };

        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int i = 0; i < lines.Length;)
        {
            string trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                document.Blocks.Add(CodeBlock(lines, ref i));
                continue;
            }

            if (IsRule(trimmed))
            {
                document.Blocks.Add(Rule());
                i++;
                continue;
            }

            if (HeadingLevel(trimmed) is { } level)
            {
                document.Blocks.Add(Heading(trimmed[(level + 1)..].Trim(), level));
                i++;
                continue;
            }

            if (IsBullet(trimmed) || IsNumbered(trimmed))
            {
                document.Blocks.Add(BuildList(lines, ref i));
                continue;
            }

            if (trimmed[0] == '>')
            {
                document.Blocks.Add(Quote(lines, ref i));
                continue;
            }

            document.Blocks.Add(BuildParagraph(lines, ref i));
        }

        return document;
    }

    /// <summary>
    /// Consecutive non-empty lines, joined with a space.
    ///
    /// Markdown's soft wrap: a paragraph hard-wrapped at 90 columns in the file is one paragraph on
    /// screen, which is what lets the help file stay comfortable to edit in a text editor.
    /// </summary>
    private static Paragraph BuildParagraph(string[] lines, ref int index)
    {
        var text = new StringBuilder();

        while (index < lines.Length)
        {
            string trimmed = lines[index].Trim();

            if (trimmed.Length == 0 || trimmed[0] == '>' ||
                HeadingLevel(trimmed) is not null ||
                IsRule(trimmed) || IsBullet(trimmed) || IsNumbered(trimmed) ||
                trimmed.StartsWith("```", StringComparison.Ordinal))
                break;

            if (text.Length > 0)
                text.Append(' ');

            text.Append(trimmed);
            index++;
        }

        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        AppendInline(paragraph.Inlines, text.ToString());
        return paragraph;
    }

    private static Paragraph Heading(string text, int level)
    {
        var paragraph = new Paragraph
        {
            FontSize = level switch { 1 => 18, 2 => 15, 3 => 13.5, _ => 12.5 },
            FontWeight = FontWeights.SemiBold,

            //More space above than below: a heading belongs to what follows it, and the gap is what
            //says so without needing a rule or a colour.
            Margin = new Thickness(0, level == 1 ? 0 : 16, 0, 6),
        };

        AppendInline(paragraph.Inlines, text);
        return paragraph;
    }

    private static List BuildList(string[] lines, ref int index)
    {
        bool numbered = IsNumbered(lines[index].Trim());

        var list = new List
        {
            MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(20, 0, 0, 0),
        };

        while (index < lines.Length)
        {
            string trimmed = lines[index].Trim();

            if (!(numbered ? IsNumbered(trimmed) : IsBullet(trimmed)))
                break;

            string content = numbered
                ? trimmed[(trimmed.IndexOf('.') + 1)..].Trim()
                : trimmed[1..].Trim();

            index++;

            //An indented follow-on line continues this item rather than starting a new block, so a
            //bullet wrapped in the file stays one bullet on screen.
            while (index < lines.Length &&
                   lines[index].Length > 0 &&
                   char.IsWhiteSpace(lines[index][0]) &&
                   lines[index].Trim() is { Length: > 0 } continuation &&
                   !IsBullet(continuation) &&
                   !IsNumbered(continuation))
            {
                content += " " + continuation;
                index++;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AppendInline(paragraph.Inlines, content);

            list.ListItems.Add(new ListItem(paragraph));
        }

        return list;
    }

    private static Section Quote(string[] lines, ref int index)
    {
        var text = new StringBuilder();

        while (index < lines.Length && lines[index].Trim().StartsWith('>'))
        {
            if (text.Length > 0)
                text.Append(' ');

            text.Append(lines[index].Trim()[1..].Trim());
            index++;
        }

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        AppendInline(paragraph.Inlines, text.ToString());

        return new Section(paragraph)
        {
            BorderBrush = Brush("BorderStrong", Brushes.Gray),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 4, 0, 4),
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = Brush("TextMuted", Brushes.Gray),
        };
    }

    private static Paragraph CodeBlock(string[] lines, ref int index)
    {
        //Past the opening fence. The info string after it (```bash) is a language hint, and there is
        //no syntax highlighting here to hand it to.
        index++;

        var text = new StringBuilder();

        while (index < lines.Length && !lines[index].Trim().StartsWith("```", StringComparison.Ordinal))
        {
            if (text.Length > 0)
                text.Append('\n');

            text.Append(lines[index]);
            index++;
        }

        //Past the closing fence when there is one. An unterminated block runs to the end of the file
        //rather than throwing.
        if (index < lines.Length)
            index++;

        return new Paragraph(new Run(text.ToString()))
        {
            FontFamily = new FontFamily(Monospace),
            FontSize = 11.5,
            Background = Brush("SurfaceAlt", Brushes.WhiteSmoke),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12),
        };
    }

    private static BlockUIContainer Rule() =>
        new(new Border
        {
            Height = 1,
            Background = Brush("Border", Brushes.LightGray),
        })
        {
            Margin = new Thickness(0, 4, 0, 14),
        };

    /// <summary>
    /// The four inline forms, scanned in one pass.
    ///
    /// Hand-scanned rather than pattern-matched: the forms nest -- a link label may be bold -- and
    /// an unclosed marker has to survive as the literal character it is. That is a state machine
    /// either way, and this is the version that can be read.
    /// </summary>
    private static void AppendInline(InlineCollection target, string text)
    {
        var pending = new StringBuilder();

        void Flush()
        {
            if (pending.Length == 0)
                return;

            target.Add(new Run(pending.ToString()));
            pending.Clear();
        }

        for (int i = 0; i < text.Length;)
        {
            char c = text[i];

            if (c == '`' && Closing(text, i + 1, "`") is { } code)
            {
                Flush();

                target.Add(new Run(text[(i + 1)..code])
                {
                    FontFamily = new FontFamily(Monospace),
                    FontSize = 11.5,
                    Background = Brush("SurfaceAlt", Brushes.WhiteSmoke),
                });

                i = code + 1;
                continue;
            }

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' && Closing(text, i + 2, "**") is { } strong)
            {
                Flush();

                var bold = new Bold();
                AppendInline(bold.Inlines, text[(i + 2)..strong]);
                target.Add(bold);

                i = strong + 2;
                continue;
            }

            if ((c == '*' || c == '_') && Closing(text, i + 1, c.ToString()) is { } emphasis)
            {
                Flush();

                var italic = new Italic();
                AppendInline(italic.Inlines, text[(i + 1)..emphasis]);
                target.Add(italic);

                i = emphasis + 1;
                continue;
            }

            if (c == '[' && Closing(text, i + 1, "](") is { } label && Closing(text, label + 2, ")") is { } close)
            {
                Flush();
                target.Add(Link(text[(i + 1)..label], text[(label + 2)..close]));

                i = close + 1;
                continue;
            }

            pending.Append(c);
            i++;
        }

        Flush();
    }

    /// <summary>
    /// Where <paramref name="marker"/> next occurs at or after <paramref name="from"/>, or null.
    ///
    /// Null also for an empty span: <c>``</c> and <c>**</c> are two literal characters the user
    /// typed, not an emphasis of nothing.
    /// </summary>
    private static int? Closing(string text, int from, string marker)
    {
        if (from >= text.Length)
            return null;

        int at = text.IndexOf(marker, from, StringComparison.Ordinal);

        return at > from ? at : null;
    }

    private static Hyperlink Link(string label, string url)
    {
        var link = new Hyperlink(new Run(label))
        {
            Foreground = Brush("Accent", Brushes.Blue),
            ToolTip = url,
        };

        //A relative or malformed target stays visible text with no click behaviour, rather than
        //throwing when the user clicks it.
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += OnNavigate;
        }

        return link;
    }

    /// <summary>
    /// Opens the link in whatever the user browses with.
    ///
    /// <c>UseShellExecute</c> is the whole mechanism. The failure is swallowed on purpose: a machine
    /// with no handler for http is a real configuration, and a help page is not where to report it.
    /// </summary>
    private static void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            _ = started;
        }
        catch (Exception)
        {
            //Nothing to do, and nowhere useful to say it.
        }

        e.Handled = true;
    }

    /// <summary>The <c>#</c> count of a heading line, or null when the line is not one.</summary>
    private static int? HeadingLevel(string trimmed)
    {
        int level = 0;

        while (level < trimmed.Length && trimmed[level] == '#')
            level++;

        //"#nothing" is a word starting with a hash. Markdown wants the space, and so does anyone
        //writing "#1" in a sentence.
        return level is > 0 and <= 6 && level < trimmed.Length && trimmed[level] == ' ' ? level : null;
    }

    private static bool IsBullet(string trimmed) =>
        trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ';

    private static bool IsNumbered(string trimmed)
    {
        int digits = 0;

        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
            digits++;

        return digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ';
    }

    private static bool IsRule(string trimmed) =>
        trimmed.Length >= 3 &&
        (trimmed.All(c => c == '-') || trimmed.All(c => c == '*') || trimmed.All(c => c == '_'));

    /// <summary>
    /// A brush from the application palette, with a fallback.
    ///
    /// The fallback is not defensive clutter: <see cref="Application.Current"/> is null in a
    /// designer and in any launch that has not built one, and a null brush there would throw rather
    /// than merely look wrong.
    /// </summary>
    private static Brush Brush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
