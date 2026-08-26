using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace VRCAvatarChanger;

// VRChat 連動: Windows 起動時にトレイで待機し、VRChat の起動を検知したらウィンドウを開く。
// 通信は一切増えない (ローカルのプロセス名チェックのみ)。スタートアップ登録は HKCU の Run キー (管理者権限不要)。
public partial class MainWindow
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VRCAvatarChanger";

    private Forms.NotifyIcon? _trayIcon;
    private System.Windows.Threading.DispatcherTimer? _watchTimer;
    private bool _vrchatWasRunning;
    private bool _exitRequested;

    /// <summary>--tray 起動時に表示せず待機してよいか (App.xaml.cs が参照)。</summary>
    internal bool WatchVRChatEnabled => _settings.WatchVRChat;

    /// <summary>ctor から一度だけ呼ぶ。設定が ON なら常駐一式 (トレイ・監視・スタートアップ登録) を整える。</summary>
    private void InitWatchVRChat()
    {
        WatchToggle.IsChecked = _settings.WatchVRChat;
        if (_settings.WatchVRChat) EnableWatch();
        Closing += (_, e) =>
        {
            // 連動 ON のときは閉じる = トレイへ (監視を続ける)。トレイの「終了」で本当に終了する。
            // OS のシャットダウンや自動更新の再起動では WPF が Cancel を無視するので閉じられる
            if (_settings.WatchVRChat && !_exitRequested)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) => { _watchTimer?.Stop(); _trayIcon?.Dispose(); };
    }

    private void WatchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.WatchVRChat = WatchToggle.IsChecked == true;
        if (!_preview) _settings.Save();
        if (_settings.WatchVRChat)
        {
            EnableWatch();
            SetStatus(StatusKind.Info, "VRChat 連動: オン。Windows 起動時にトレイで待機し、VRChat が起動したら自動で開きます");
        }
        else
        {
            DisableWatch();
            SetStatus(StatusKind.Info, "VRChat 連動: オフ。スタートアップ登録も解除しました");
        }
    }

    private void EnableWatch()
    {
        if (!_preview) RegisterStartup();
        if (_trayIcon is null)
        {
            _trayIcon = new Forms.NotifyIcon
            {
                Text = "VRCAvatarChanger",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application,
                Visible = true,
            };
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("開く", null, (_, _) => ShowFromTray());
            menu.Items.Add("終了", null, (_, _) => { _exitRequested = true; Close(); });
            _trayIcon.ContextMenuStrip = menu;
        }
        if (_watchTimer is null)
        {
            _watchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _watchTimer.Tick += (_, _) => WatchTick();
        }
        _vrchatWasRunning = IsVRChatRunning(); // 有効化した時点で既に起動中なら、それを理由には開かない
        _watchTimer.Start();
    }

    private void DisableWatch()
    {
        UnregisterStartup();
        _watchTimer?.Stop();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private void WatchTick()
    {
        var running = IsVRChatRunning();
        // 「未起動 → 起動」の変化のときだけ開く (VRChat 起動中に手で閉じたウィンドウを勝手に開き直さない)
        if (running && !_vrchatWasRunning && !IsVisible) ShowFromTray();
        _vrchatWasRunning = running;
    }

    private static bool IsVRChatRunning()
    {
        var ps = Process.GetProcessesByName("VRChat");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---- Windows スタートアップ登録 (HKCU、現在のユーザーのみ・管理者権限不要) ----

    private static void RegisterStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            // exe を移動しても追従するよう、連動が ON の間は毎回書き直す
            key?.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --tray");
        }
        catch { /* 書けない環境ではトレイ常駐だけ有効 */ }
    }

    private static void UnregisterStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
