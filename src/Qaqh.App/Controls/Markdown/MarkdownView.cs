using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Qaqh_App.Controls.Markdown;

/// <summary>
/// markdown → 原生控件树的视图（StackPanel 子类）。Text 变化后 60ms 去抖重绘，
/// 适配聊天流式追加；代码块/行内代码/列表/引用/链接全走原生控件。
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private DispatcherQueueTimer? _debounce;

    public MarkdownView()
    {
        Spacing = 2;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)d;
        view._debounce ??= view.DispatcherQueue.CreateTimer();
        view._debounce.Interval = TimeSpan.FromMilliseconds(60);
        view._debounce.IsRepeating = false;
        view._debounce.Tick += (_, _) => RenderNow(view);
        view._debounce.Stop();
        view._debounce.Start();
    }

    private static void RenderNow(MarkdownView view)
    {
        view.Children.Clear();
        foreach (var element in MarkdownRenderer.Render(view.Text))
        {
            view.Children.Add(element);
        }
    }
}
