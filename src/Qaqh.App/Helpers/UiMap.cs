using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

/// <summary>会话活动状态 → 字形 / 颜色 / 文案的静态映射（x:Bind 函数用）。</summary>
public static class UiMap
{
    public static string SessionGlyph(SessionRunStatus state) => state switch
    {
        SessionRunStatus.Running => "\uE768",          // play / 运行
        SessionRunStatus.WaitingUser => "\uE7BA",      // error-like attention → 用人形提醒
        SessionRunStatus.Cancelling => "\uE711",       // cancel
        SessionRunStatus.Idle => "\uE738",             // pause-ish / 空闲
        _ => "\uEA3A",                                 // not running
    };

    public static Brush SessionBrush(SessionRunStatus state) => ThemeBrush(state switch
    {
        SessionRunStatus.Running => "SystemFillColorSuccessBrush",
        SessionRunStatus.WaitingUser => "SystemFillColorAttentionBrush",
        SessionRunStatus.Cancelling => "SystemFillColorCautionBrush",
        SessionRunStatus.Idle => "TextFillColorSecondaryBrush",
        _ => "TextFillColorDisabledBrush",
    });

    public static string SessionStateText(SessionRunStatus state) => state switch
    {
        SessionRunStatus.Running => "运行中",
        SessionRunStatus.WaitingUser => "等待输入",
        SessionRunStatus.Cancelling => "取消中",
        SessionRunStatus.Idle => "空闲",
        _ => "未运行",
    };

    public static Brush ConnectionBrush(ConnectionKind kind) => ThemeBrush(kind switch
    {
        ConnectionKind.Connected => "SystemFillColorSuccessBrush",
        ConnectionKind.Connecting => "SystemFillColorAttentionBrush",
        ConnectionKind.Error => "SystemFillColorCriticalBrush",
        _ => "TextFillColorDisabledBrush",
    });

    private static Brush ThemeBrush(string key) =>
        (Brush)Application.Current.Resources[key];
}

public enum ConnectionKind
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}
