using System.Windows;

namespace VRCAvatarChanger;

/// <summary>
/// 設定ウィンドウ。設定値の適用 (保存や常駐の切り替え) は MainWindow 側のロジックに委ねる。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Action<bool> _applyWatchVRChat;
    private readonly bool _ready;

    public SettingsWindow(Settings settings, Action<bool> applyWatchVRChat)
    {
        _applyWatchVRChat = applyWatchVRChat;
        InitializeComponent();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        WatchToggle.IsChecked = settings.WatchVRChat;
        _ready = true;
    }

    private void WatchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _applyWatchVRChat(WatchToggle.IsChecked == true);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
