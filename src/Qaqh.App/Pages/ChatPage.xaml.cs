using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Qaqh_App;
using Qaqh.Sdk.Contract;
using Windows.System;

namespace Qaqh_App.Pages;

public sealed partial class ChatPageInput : ObservableObject
{
    [ObservableProperty]
    private string _draft = "";
}

/// <summary>聊天页导航参数。</summary>
public sealed record ChatPageArgs(string SessionId);

/// <summary>聊天流条目 → 数据模板分流。</summary>
public sealed class ChatTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Assistant { get; set; }
    public DataTemplate? Tool { get; set; }
    public DataTemplate? Approval { get; set; }
    public DataTemplate? Exec { get; set; }
    public DataTemplate? Subagent { get; set; }
    public DataTemplate? Divider { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => item switch
    {
        ChatUserVm => User,
        ChatAssistantVm => Assistant,
        ChatToolVm => Tool,
        ChatApprovalVm => Approval,
        ChatExecVm => Exec,
        ChatSubagentVm => Subagent,
        ChatDividerVm => Divider,
        _ => null,
    };
}

public sealed partial class ChatPage : Page
{
    private readonly ChatPageInput _input = new();

    /// <summary>当前展示的会话（与壳层选中会话保持同引用）。</summary>
    public SessionItemVm Session { get; private set; } = new();

    /// <summary>当前会话的聊天视图模型（bootstrap 合流 + 增量）。</summary>
    public ChatViewModel Chat { get; private set; } = null!;

    private ChatPageInput Vm => _input;

    /// <summary>会话任务面板。</summary>
    public TodoPanelVm Todos { get; private set; } = null!;

    /// <summary>会话团队面板。</summary>
    public TeamPanelVm Team { get; private set; } = null!;

    public ChatPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var sessionId = e.Parameter switch
        {
            ChatPageArgs args => args.SessionId,
            string id => id,
            _ => null,
        } ?? App.Shell.SelectedSession?.SessionId ?? "";
        Session = App.Shell.Sessions.FirstOrDefault(s => s.SessionId == sessionId)
            ?? new SessionItemVm { SessionId = sessionId, Title = $"会话 {sessionId[^8..]}" };
        Chat = App.Shell.GetChat(sessionId);
        Todos = new TodoPanelVm(App.Shell, sessionId);
        Team = new TeamPanelVm(App.Shell, sessionId);
        Bindings.Update();
        _ = RefreshTodosAsync();
        _ = RefreshTeamAsync();

        // 条目增删时滚到底（流式追加/新消息）。
        Chat.Items.CollectionChanged += OnItemsChanged;
        if (!Chat.Loaded)
        {
            _ = LoadChatAsync(sessionId);
        }
        InputBox.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Chat.Items.CollectionChanged -= OnItemsChanged;
    }

    private async Task LoadChatAsync(string sessionId)
    {
        try
        {
            await App.Shell.EnsureChatLoadedAsync(sessionId);
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "加载会话失败",
                Content = ex.Message,
                CloseButtonText = "关闭",
            };
            await dialog.ShowAsync();
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && Chat.Items.Count > 0)
        {
            StreamList.ScrollIntoView(Chat.Items[^1]);
        }
    }

    // ── 输入 ──────────────────────────────────────────────────────────

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await SendAsync();
    }

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var text = _input.Draft.Trim();
        if (text.Length == 0 || Session is null)
        {
            return;
        }
        _input.Draft = "";
        Bindings.Update();
        try
        {
            await App.Shell.SendMessageAsync(Session.SessionId, text);
        }
        catch (Exception ex)
        {
            _input.Draft = text; // 发送失败回滚草稿
            Bindings.Update();
            await ShowErrorAsync("发送失败", ex);
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Session is null)
        {
            return;
        }
        try
        {
            await App.Shell.CancelTurnAsync(Session.SessionId);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("取消失败", ex);
        }
    }

    // ── 任务面板 ──────────────────────────────────────────────────────

    private async Task RefreshTodosAsync()
    {
        if (Todos is null)
        {
            return;
        }
        try
        {
            await Todos.RefreshAsync();
        }
        catch (Exception)
        {
            // todo 查询失败不打断聊天（会话可能尚未物化）。
        }
    }

    private async void TodoRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshTodosAsync();
    }

    private async void TodoCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }
        try
        {
            await Todos.CancelAsync(id);
            await RefreshTodosAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("取消任务失败", ex);
        }
    }

    private async Task RefreshTeamAsync()
    {
        if (Team is null)
        {
            return;
        }
        await Team.RefreshAsync();
        Bindings.Update();
    }

    private async void TeamRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshTeamAsync();
    }

    // ── exec 终端卡 ───────────────────────────────────────────────────

    private async void ExecInterrupt_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatExecVm exec)
        {
            return;
        }
        try
        {
            await App.Shell.InterruptExecAsync(exec.ExecSessionId);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("中断失败", ex);
        }
    }

    private async void ExecTerminate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatExecVm exec)
        {
            return;
        }
        try
        {
            await App.Shell.TerminateExecAsync(exec.ExecSessionId);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("终止失败", ex);
        }
    }

    // ── 审批卡响应 ────────────────────────────────────────────────────

    private async void ApprovalAllow_Click(object sender, RoutedEventArgs e) =>
        await RespondPermission(sender, approved: true);

    private async void ApprovalDeny_Click(object sender, RoutedEventArgs e) =>
        await RespondPermission(sender, approved: false);

    private async Task RespondPermission(object sender, bool approved)
    {
        if (ApprovalOf(sender) is not { } approval)
        {
            return;
        }
        try
        {
            await App.Shell.RespondPermissionAsync(
                Session.SessionId, approval.CallId, approved, approval.TrustFolder);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("审批响应失败", ex);
        }
    }

    private async void AskAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (ApprovalOf(sender) is not { } approval)
        {
            return;
        }
        var answer = AskBoxOf(sender)?.Text.Trim();
        if (string.IsNullOrEmpty(answer))
        {
            return;
        }
        try
        {
            await App.Shell.RespondAskAsync(Session.SessionId, approval.InteractionId, answer);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("回答发送失败", ex);
        }
    }

    private async void AskDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (ApprovalOf(sender) is not { } approval)
        {
            return;
        }
        try
        {
            await App.Shell.DismissAskAsync(Session.SessionId, approval.InteractionId);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("忽略失败", ex);
        }
    }

    // DataTemplate 内的按钮 → 同模板根 Border → DataContext = ChatApprovalVm。
    private static ChatApprovalVm? ApprovalOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ChatApprovalVm;

    private static TextBox? AskBoxOf(object sender) =>
        ((sender as FrameworkElement)?.Parent as StackPanel)?
            .Children.OfType<TextBox>()
            .FirstOrDefault(tb => tb.Name == "AskAnswerBox");

    private async Task ShowErrorAsync(string title, Exception ex)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = ex.Message,
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }
}
