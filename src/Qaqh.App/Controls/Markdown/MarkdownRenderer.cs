using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Qaqh_App.Controls.Markdown;

/// <summary>
/// markdown AST → XAML 控件树（纯原生：RichTextBlock / Border / TextBlock）。
/// 聊天流场景：一条消息一次 Render，流式期间整条重绘（调用方节流）。
/// </summary>
public static class MarkdownRenderer
{
    public static IList<UIElement> Render(string markdown)
    {
        var elements = new List<UIElement>();
        foreach (var block in MarkdownParser.ParseBlocks(markdown))
        {
            switch (block)
            {
                case MdHeading heading:
                    elements.Add(RenderHeading(heading));
                    break;
                case MdParagraph paragraph:
                    elements.Add(RenderParagraph(paragraph));
                    break;
                case MdCodeFence code:
                    elements.Add(RenderCodeFence(code));
                    break;
                case MdListItem item:
                    elements.Add(RenderListItem(item));
                    break;
                case MdQuote quote:
                    elements.Add(RenderQuote(quote));
                    break;
                case MdRule:
                    elements.Add(new Rectangle
                    {
                        Height = 1,
                        Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                        Margin = new Thickness(0, 6, 0, 6),
                    });
                    break;
            }
        }
        return elements;
    }

    private static UIElement RenderHeading(MdHeading heading)
    {
        var rich = new RichTextBlock();
        var paragraph = new Paragraph { Margin = new Thickness(0, 6, 0, 4) };
        var span = new Span();
        FillInline(span.Inlines, heading.Inline);
        ApplyFontSize(span, heading.Level switch
        {
            1 => 24.0,
            2 => 20.0,
            3 => 17.0,
            _ => 15.0,
        });
        paragraph.Inlines.Add(span);
        rich.Blocks.Add(paragraph);
        return rich;
    }

    private static UIElement RenderParagraph(MdParagraph paragraph)
    {
        var rich = new RichTextBlock();
        var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
        if (paragraph.Inline is MdSequence seq)
        {
            foreach (var item in seq.Items)
            {
                AppendInline(p.Inlines, item);
            }
        }
        else
        {
            AppendInline(p.Inlines, paragraph.Inline);
        }
        rich.Blocks.Add(p);
        return rich;
    }

    private static UIElement RenderCodeFence(MdCodeFence code)
    {
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CodeBlockBackgroundBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock
                {
                    Text = code.Text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.NoWrap,
                    IsTextSelectionEnabled = true,
                },
            },
        };
    }

    private static UIElement RenderListItem(MdListItem item)
    {
        var marker = new TextBlock
        {
            Text = item.Ordered ? $"{item.Index}." : "•",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 6, 0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var content = new RichTextBlock();
        var p = new Paragraph { Margin = new Thickness(0) };
        AppendInline(p.Inlines, item.Inline);
        content.Blocks.Add(p);
        var grid = new Grid { Margin = new Thickness(10, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(marker, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(marker);
        grid.Children.Add(content);
        return grid;
    }

    private static UIElement RenderQuote(MdQuote quote)
    {
        var bar = new Border
        {
            Width = 3,
            Margin = new Thickness(0, 2, 8, 2),
            Background = (Brush)Application.Current.Resources["ControlAccentColorDisabledBrush"],
        };
        var content = new RichTextBlock { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var p = new Paragraph();
        AppendInline(p.Inlines, quote.Inline);
        content.Blocks.Add(p);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(bar);
        grid.Children.Add(content);
        return grid;
    }

    // ── 行内 ──────────────────────────────────────────────────────────

    private static void FillInline(InlineCollection inlines, MdInline inline)
    {
        if (inline is MdSequence seq)
        {
            foreach (var item in seq.Items)
            {
                AppendInline(inlines, item);
            }
        }
        else
        {
            AppendInline(inlines, inline);
        }
    }

    private static void AppendInline(InlineCollection inlines, MdInline inline)
    {
        switch (inline)
        {
            case MdText text:
                inlines.Add(new Run { Text = text.Value });
                break;
            case MdBold bold:
                var boldSpan = new Span { FontWeight = FontWeights.Bold };
                FillInline(boldSpan.Inlines, bold.Inner);
                inlines.Add(boldSpan);
                break;
            case MdItalic italic:
                var italicSpan = new Span { FontStyle = Windows.UI.Text.FontStyle.Italic };
                FillInline(italicSpan.Inlines, italic.Inner);
                inlines.Add(italicSpan);
                break;
            case MdInlineCode code:
                inlines.Add(new InlineUIContainer
                {
                    Child = new Border
                    {
                        Background = (Brush)Application.Current.Resources["CodeInlineBackgroundBrush"],
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 0, 4, 1),
                        Child = new TextBlock
                        {
                            Text = code.Code,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12.5,
                        },
                    },
                });
                break;
            case MdLink link:
                var hyperlink = new Hyperlink { NavigateUri = new Uri(link.Url) };
                hyperlink.Inlines.Add(new Run { Text = link.Text });
                hyperlink.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(hyperlink.NavigateUri);
                inlines.Add(hyperlink);
                break;
            case MdSequence nested:
                foreach (var item in nested.Items)
                {
                    AppendInline(inlines, item);
                }
                break;
        }
    }

    private static void ApplyFontSize(Span span, double size) => span.FontSize = size;
}
