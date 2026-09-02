using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace VRCAvatarChanger;

/// <summary>
/// 埋め込みブラウザで vrchat.com のログイン画面を開き、Discord / Google 等の外部連携ログインを済ませてもらう。
/// ログイン完了後、ブラウザが持つ auth / twoFactorAuth クッキーを取り出して API セッションに採用する。
/// </summary>
public partial class BrowserLoginWindow : Window
{
    private const string LoginUrl = "https://vrchat.com/home/login";
    private static readonly string[] CookieOrigins = ["https://vrchat.com", "https://api.vrchat.cloud"];

    private readonly VRChatApi _api;
    private readonly bool _keepLogin;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private (string? Auth, string? TwoFactor) _lastTried;
    private bool _verifying;

    /// <summary>ログインに成功したときのユーザー。キャンセル時は null。</summary>
    public CurrentUser? Result { get; private set; }

    /// <param name="keepLogin">true ならログイン成功後もブラウザのログイン状態 (VRChat / Discord / Google) を残す。</param>
    public BrowserLoginWindow(VRChatApi api, bool keepLogin)
    {
        _api = api;
        _keepLogin = keepLogin;
        InitializeComponent();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => _poll.Stop();
        _poll.Tick += async (_, _) => await CheckCookiesAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.In("WebView2"));
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;

            // ログイン専用の最小権限ブラウザにする
            var s = core.Settings;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsBuiltInErrorPageEnabled = true;

            // https 以外への遷移は拒否
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) e.Cancel = true;
            };
            // OAuth のポップアップは同じウィンドウ内で開く(別ウィンドウ・外部ブラウザには出さない)
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) core.Navigate(e.Uri);
            };
            // ダウンロードは不要なので全て拒否
            core.DownloadStarting += (_, e) => e.Cancel = true;
            // 権限要求(カメラ・位置情報など)は全て拒否
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;

            core.NavigationCompleted += async (_, _) => await CheckCookiesAsync();
            core.Navigate(LoginUrl);
            _poll.Start();
            StatusText.Text = "ログインを待っています";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "埋め込みブラウザ (WebView2 ランタイム) を起動できませんでした。\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/ からランタイムをインストールしてください。\n\n" + ex.Message,
                "VRCAvatarChanger", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async Task CheckCookiesAsync()
    {
        if (_verifying || Web.CoreWebView2 is null) return;
        _verifying = true;
        try
        {
            string? auth = null, twoFactor = null;
            foreach (var origin in CookieOrigins)
            {
                var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(origin);
                foreach (var c in cookies)
                {
                    if (c.Name == "auth") auth ??= c.Value;
                    else if (c.Name == "twoFactorAuth") twoFactor ??= c.Value;
                }
            }
            if (auth is null || (auth, twoFactor) == _lastTried) return;
            _lastTried = (auth, twoFactor);

            StatusText.Text = "セッションを確認しています";
            _api.SetSessionCookies(auth, twoFactor);
            var user = await _api.TryGetCurrentUserAsync();
            if (user is not null)
            {
                Result = user;
                _poll.Stop();
                // セッションはアプリ側 (DPAPI 暗号化) に移した。
                // 「ログイン状態を保持」がオフのときだけ、従来どおりブラウザ側のクッキーを消す
                if (!_keepLogin) await ClearBrowserDataAsync(Web.CoreWebView2);
                DialogResult = true;
                Close();
                return;
            }
            StatusText.Text = "ログインを待っています。2 段階認証が必要な場合は、そのままブラウザ内で入力してください。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "セッションを確認できませんでした: " + FriendlyError.Of(ex);
        }
        finally { _verifying = false; }
    }

    private static async Task ClearBrowserDataAsync(CoreWebView2 core)
    {
        try
        {
            core.CookieManager.DeleteAllCookies();
            await core.Profile.ClearBrowsingDataAsync();
        }
        catch (Exception ex) { Log.Debug("ブラウザのクッキーを消せませんでした", ex); }
    }

    /// <summary>ログアウト時など、ブラウザプロファイルをディスクごと削除する。</summary>
    public static void DeleteBrowserProfile()
    {
        try
        {
            var dir = AppPaths.In("WebView2");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { Log.Debug("ブラウザのプロファイルを消せませんでした (使用中なら次回に)", ex); }
    }
}
