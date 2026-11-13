using Microsoft.UI.Xaml;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

/// <summary>聊天模板的 x:Bind 静态函数。不能挂在 Page 自类——XAML 编译器
/// 对「页面自身类的静态函数引用」会内崩（WMC9999）。</summary>
public static class ChatUi
{
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNotEmpty(string value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static string ApprovalTitle(SessionInteractionKind kind) => kind switch
    {
        SessionInteractionKind.Permission => "工具执行请求",
        SessionInteractionKind.Ask => "需要你的输入",
        _ => "交互请求",
    };
}
