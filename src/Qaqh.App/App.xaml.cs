using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace Qaqh_App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    /// <summary>应用壳状态中枢（UI 线程创建，单例）。</summary>
    public static ShellViewModel Shell { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Shell = new ShellViewModel(DispatcherQueue.GetForCurrentThread());
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
