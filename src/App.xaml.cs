using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace VRCAvatarChanger;

public partial class App : Application
{
    public static bool IsDarkTheme { get; private set; } = true;

    private const string InstanceMutexName = "VRCAvatarChanger.SingleInstance.v1";
    private const string ActivateEventName = "VRCAvatarChanger.Activate.v1";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 二重起動の防止: すでに起動していれば、そちらを前面に出して終了する(OSC ポートの取り合いも防ぐ)
        var allowMulti = Environment.GetEnvironmentVariable("VRCAC_ALLOW_MULTI") == "1"; // 開発・検証用
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirst);
        if (!isFirst && !allowMulti)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Dispatcher.BeginInvoke(ActivateMainWindow), null, -1, false);

        Updater.CleanupOldVersion();
        ApplySystemTheme();
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            LogError(args.Exception);
            if (_showingError) return; // ダイアログ表示中の再入(レイアウト例外の連鎖など)は握りつぶす
            _showingError = true;
            try
            {
                MessageBox.Show(
                    "予期しないエラーが起きました。操作をやり直しても直らない場合は、アプリを再起動してください。\n\n" +
                    "詳細: " + args.Exception.Message + "\n\n記録先: " + ErrorLogPath,
                    "VRCAvatarChanger", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _showingError = false; }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => { args.SetObserved(); LogError(args.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);

        var window = new MainWindow();
        MainWindow = window;
        // スタートアップ登録 (--tray) からの起動で VRChat 連動が ON なら、表示せずトレイ待機から始める
        if (!(e.Args.Contains("--tray") && window.WatchVRChatEnabled)) window.Show();
    }

    private static bool _showingError;

    public static readonly string ErrorLogPath = AppPaths.In("error.log");

    /// <summary>予期しない例外を %AppData%\VRCAvatarChanger\error.log に追記する(問い合わせ対応用。個人情報は含めない)。</summary>
    public static void LogError(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ErrorLogPath)!);
            // 肥大化防止: 512KB を超えたら作り直す
            if (System.IO.File.Exists(ErrorLogPath) && new System.IO.FileInfo(ErrorLogPath).Length > 512 * 1024) System.IO.File.Delete(ErrorLogPath);
            System.IO.File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }

    private void ActivateMainWindow()
    {
        var w = MainWindow;
        if (w is null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true; w.Topmost = false; // 確実に前面へ
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Windows の「アプリのモード」(ダーク/ライト)に合わせてテーマ辞書を差し替える。</summary>
    private void ApplySystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            IsDarkTheme = key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch { IsDarkTheme = true; }

        // 環境変数 VRCAC_THEME=light / dark で強制できる
        switch (Environment.GetEnvironmentVariable("VRCAC_THEME")?.ToLowerInvariant())
        {
            case "light": IsDarkTheme = false; break;
            case "dark": IsDarkTheme = true; break;
        }

        var dict = new ResourceDictionary { Source = new Uri(IsDarkTheme ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
    }

    /// <summary>タイトルバーもテーマに合わせる (DWM の immersive dark mode)。ウィンドウの SourceInitialized 以降で呼ぶ。</summary>
    public static void ApplyTitleBarTheme(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDarkTheme ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch { /* 古い Windows では未対応。無視 */ }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
