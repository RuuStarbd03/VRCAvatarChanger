using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VRCAvatarChanger;

// VRChat 連動: Windows 起動時にトレイで待機し、VRChat の起動を検知したらウィンドウを開く。
// 通信は一切増えない (ローカルのプロセス名チェックのみ)。スタートアップ登録は HKCU の Run キー (管理者権限不要)。
public partial class MainWindow
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VRCAvatarChanger";

    // プロセス一覧の取得は 1 回あたり数ミリ秒かかる (実測 6.5ms / 350 プロセス)。常駐している間ずっと
    // 走らせるものなので、間隔は広めにとる。VRChat は起動からログイン画面まで数十秒かかるため、
    // 15 秒間隔でも「起動したら開く」体験は変わらない。
    private static readonly TimeSpan WatchIdleInterval = TimeSpan.FromSeconds(15);
    // 起動中は「終了したか」を見るだけで急がないので、さらに間隔を空ける
    private static readonly TimeSpan WatchRunningInterval = TimeSpan.FromSeconds(60);

    private TrayIcon? _trayIcon;
    private System.Windows.Threading.DispatcherTimer? _watchTimer;
    private bool _vrchatWasRunning;
    private bool _exitRequested;

    /// <summary>--tray 起動時に表示せず待機してよいか (App.xaml.cs が参照)。</summary>
    internal bool WatchVRChatEnabled => _settings.WatchVRChat;

    /// <summary>ctor から一度だけ呼ぶ。設定が ON なら常駐一式 (トレイ・監視・スタートアップ登録) を整える。</summary>
    private void InitWatchVRChat()
    {
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

    /// <summary>設定ウィンドウからの切り替えを適用する (保存・スタートアップ登録・常駐の開始/終了)。</summary>
    internal void SetWatchVRChat(bool enabled)
    {
        if (_settings.WatchVRChat == enabled) return;
        _settings.WatchVRChat = enabled;
        SaveSettings();
        if (enabled)
        {
            EnableWatch();
            SetStatus(StatusKind.Info, "VRChat 連動: オン");
        }
        else
        {
            DisableWatch();
            SetStatus(StatusKind.Info, "VRChat 連動: オフ");
        }
    }

    private void EnableWatch()
    {
        if (!_preview) RegisterStartup();
        if (_trayIcon is null)
        {
            var open = new MenuItem { Header = "開く" };
            open.Click += (_, _) => ShowFromTray();
            var exit = new MenuItem { Header = "終了" };
            exit.Click += (_, _) => { _exitRequested = true; Close(); };
            var menu = new ContextMenu();
            menu.Items.Add(open);
            menu.Items.Add(exit);
            _trayIcon = new TrayIcon(this, "VRCAvatarChanger") { Menu = menu };
            _trayIcon.DoubleClick += ShowFromTray;
        }
        if (_watchTimer is null)
        {
            _watchTimer = new System.Windows.Threading.DispatcherTimer { Interval = WatchIdleInterval };
            _watchTimer.Tick += async (_, _) => await WatchTickAsync();
        }
        _vrchatWasRunning = IsVRChatRunning(); // 有効化した時点で既に起動中なら、それを理由には開かない
        _watchTimer.Start();
    }

    private void DisableWatch()
    {
        UnregisterStartup();
        _watchTimer?.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private bool _watchChecking;

    private async Task WatchTickAsync()
    {
        // プロセスの列挙は全プロセスを見るので数 ms〜かかる。常駐中ずっと繰り返すので UI スレッドの外で行う
        if (_watchChecking) return;
        _watchChecking = true;
        bool running;
        try { running = await Task.Run(IsVRChatRunning); }
        catch (Exception ex) { Log.Debug("VRChat の起動を確認できませんでした (次の周期で再確認します)", ex); return; }
        finally { _watchChecking = false; }
        if (_watchTimer is not { IsEnabled: true }) return; // 確認している間に連動がオフにされた
        // 「未起動 → 起動」の変化のときだけ開く (VRChat 起動中に手で閉じたウィンドウを勝手に開き直さない)
        if (running && !_vrchatWasRunning && !IsVisible) ShowFromTray();
        _vrchatWasRunning = running;
        // 起動中は見に行く回数を減らす
        var interval = running ? WatchRunningInterval : WatchIdleInterval;
        if (_watchTimer is not null && _watchTimer.Interval != interval) _watchTimer.Interval = interval;
    }

    private static bool IsVRChatRunning() => Shell.IsRunning("VRChat");

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
        catch (Exception ex) { Log.Warn("Windows のスタートアップに登録できませんでした (トレイ常駐だけ有効になります)", ex); }
    }

    private static void UnregisterStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Warn("Windows のスタートアップ登録を解除できませんでした", ex); }
    }
}
