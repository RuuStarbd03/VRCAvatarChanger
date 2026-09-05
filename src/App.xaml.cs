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
        var settings = Settings.Load();
        Log.Enabled = settings.LogEnabled;
        Log.Info($"起動: v{Updater.CurrentVersion.ToString(3)} 引数=[{string.Join(" ", e.Args)}]");
        ApplyTheme(settings.Theme);
        // 「システム」を選んでいる間は、Windows 側でモードが変わったら追従する
        // (時間帯で自動的に切り替える設定があるので、開きっぱなしでも合っていてほしい)
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
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
                    "詳細: " + args.Exception.Message + "\n\n記録先: " + Log.FilePath,
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

    /// <summary>予期しない例外を app.log に残す (問い合わせ対応用。個人情報は含めない)。</summary>
    public static void LogError(Exception? ex)
    {
        if (ex is not null) Log.Error("予期しないエラー", ex);
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
        Log.Info("終了");
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>今選ばれている配色 ("system" / "light" / "dark")。</summary>
    private static string _themeMode = "system";

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _themeMode == "system")
            Current?.Dispatcher.BeginInvoke(() => ApplyTheme("system"));
    }

    /// <summary>
    /// 配色を適用する。mode は "system" (Windows のアプリのモードに合わせる) / "light" / "dark"。
    /// 色は全て DynamicResource 経由なので、辞書を差し替えれば開いている画面もその場で変わる。
    /// </summary>
    public static void ApplyTheme(string mode)
    {
        _themeMode = mode is "light" or "dark" ? mode : "system";
        IsDarkTheme = _themeMode switch
        {
            "light" => false,
            "dark" => true,
            _ => IsSystemDark(),
        };
        // 環境変数 VRCAC_THEME=light / dark で強制できる (検証用。設定より優先)
        switch (Environment.GetEnvironmentVariable("VRCAC_THEME")?.ToLowerInvariant())
        {
            case "light": IsDarkTheme = false; break;
            case "dark": IsDarkTheme = true; break;
        }

        if (Current is not { } app) return;
        var dict = new ResourceDictionary { Source = new Uri(IsDarkTheme ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        // 差し替えは 1 手で行う (一度空にすると、その瞬間だけ色を引けなくなる)
        if (app.Resources.MergedDictionaries.Count > 0) app.Resources.MergedDictionaries[0] = dict;
        else app.Resources.MergedDictionaries.Add(dict);
        // タイトルバーは DWM 側の設定なので、開いている分を塗り直す
        foreach (Window window in app.Windows) ApplyTitleBarTheme(window);
    }

    /// <summary>Windows の「アプリのモード」がダークか。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch (Exception ex) { Log.Debug("Windows のアプリのモードを読めませんでした (ダーク扱い)", ex); return true; }
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
        catch (Exception ex) { Log.Debug("タイトルバーの配色を設定できませんでした (古い Windows では未対応)", ex); }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
