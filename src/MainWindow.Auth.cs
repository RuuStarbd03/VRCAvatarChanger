using System.Windows;
using System.Windows.Input;

namespace VRCAvatarChanger;

// 認証: ログイン / 2FA / セッション復元 / ログアウトと、ログイン画面とメイン画面の行き来。
public partial class MainWindow
{
    private async Task TryRestoreSessionAsync()
    {
        if (!_api.HasSavedSession) return;
        SetLoginBusy(true, "保存されたセッションを確認中...");
        try
        {
            var user = await _api.TryGetCurrentUserAsync();
            if (user is not null) await EnterMainAsync(user);
            else LoginStatus.Text = "";
        }
        catch (Exception ex) { SetLoginStatus(FriendlyError.Of(ex), error: true); }
        finally { SetLoginBusy(false); }
    }

    private void LoginField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoginButton_Click(sender, e);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text.Trim();
        var pass = PasswordBox.Password;
        if (user.Length == 0 || pass.Length == 0)
        {
            SetLoginStatus("ユーザー名とパスワードを入力してください", error: true);
            return;
        }
        SetLoginBusy(true, "ログイン中...");
        try
        {
            var me = await _api.LoginAsync(user, pass);
            PasswordBox.Clear();
            await EnterMainAsync(me);
        }
        catch (TwoFactorRequiredException tfa)
        {
            PasswordBox.Clear();
            _twoFactorMethods = tfa.Methods;
            ShowTwoFactor();
        }
        catch (Exception ex) { SetLoginStatus(FriendlyError.Of(ex), error: true); }
        finally { SetLoginBusy(false); }
    }

    private async void BrowserLoginButton_Click(object sender, RoutedEventArgs e)
    {
        LoginStatus.Text = "";
        var win = new BrowserLoginWindow(_api, _settings.KeepBrowserLogin) { Owner = this };
        var ok = win.ShowDialog() == true && win.Result is not null;
        if (ok) await EnterMainAsync(win.Result!);
        else SetLoginStatus("ブラウザでのログインを中止しました", error: false);
    }

    // ---------------- 外部ブラウザ (Chrome など) でのログイン ----------------
    // ふだん使うブラウザで VRChat にログイン済みなら、/api/1/auth が返す token をコピーするだけで入れる。
    // 他アプリのクッキーは読まない (読めない)。トークンの受け渡しは利用者のコピー操作だけを経由する。

    private const string AuthTokenUrl = "https://api.vrchat.cloud/api/1/auth";
    private const string VRChatLoginUrl = "https://vrchat.com/home/login";

    private System.Windows.Threading.DispatcherTimer? _clipboardWatch;
    private string? _clipboardSeen;

    private void ExternalLoginButton_Click(object sender, RoutedEventArgs e)
    {
        CredentialsPanel.Visibility = Visibility.Collapsed;
        ExternalLoginPanel.Visibility = Visibility.Visible;
        LoginStatus.Text = "";
        AuthTokenBox.Clear();
        _clipboardSeen = ReadClipboard(); // 開いた時点の内容は「コピーされた」とみなさない
        StartClipboardWatch();
        OpenInBrowser(AuthTokenUrl);
    }

    private void OpenAuthPage_Click(object sender, RoutedEventArgs e) => OpenInBrowser(AuthTokenUrl);

    private void OpenVRChatSite_Click(object sender, RoutedEventArgs e) => OpenInBrowser(VRChatLoginUrl);

    private void OpenInBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetLoginStatus("ブラウザを開けませんでした: " + ex.Message, error: true); }
    }

    private void StartClipboardWatch()
    {
        if (_clipboardWatch is null)
        {
            _clipboardWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _clipboardWatch.Tick += (_, _) => CheckClipboardForToken();
        }
        _clipboardWatch.Start();
    }

    private void StopClipboardWatch() => _clipboardWatch?.Stop();

    /// <summary>
    /// ブラウザでコピーされた token を拾って自動で続行する。
    /// 見るのは「VRChat の token 形式か」だけで、それ以外のクリップボード内容は無視し、記録も送信もしない。
    /// 監視するのはこの画面を開いている間だけ。
    /// </summary>
    private void CheckClipboardForToken()
    {
        var text = ReadClipboard();
        if (text is null || text == _clipboardSeen) return;
        _clipboardSeen = text;
        if (!VRChatApi.TryParseAuthToken(text, out var token)) return;
        // 自動続行は VRChat のトークン形式 (authcookie_...) に限る。誤検知でログインを試さないため
        if (!token.StartsWith("authcookie_", StringComparison.Ordinal)) return;
        AuthTokenBox.Text = token;
        _ = LoginWithTokenAsync(token);
    }

    private static string? ReadClipboard()
    {
        // 他アプリがクリップボードを掴んでいると失敗することがある
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    private void AuthTokenField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AuthTokenLogin_Click(sender, e);
    }

    private async void AuthTokenLogin_Click(object sender, RoutedEventArgs e)
    {
        if (!VRChatApi.TryParseAuthToken(AuthTokenBox.Text, out var token))
        {
            SetLoginStatus("token を貼り付けてください(ページに表示された token の値、または表示内容をそのまま)。", error: true);
            return;
        }
        await LoginWithTokenAsync(token);
    }

    private async Task LoginWithTokenAsync(string token)
    {
        StopClipboardWatch();
        SetLoginBusy(true, "ログイン中...");
        try
        {
            var me = await _api.LoginWithAuthTokenAsync(token);
            AuthTokenBox.Clear();
            ExternalLoginPanel.Visibility = Visibility.Collapsed;
            CredentialsPanel.Visibility = Visibility.Visible;
            await EnterMainAsync(me);
        }
        catch (TwoFactorRequiredException tfa)
        {
            AuthTokenBox.Clear();
            ExternalLoginPanel.Visibility = Visibility.Collapsed;
            _twoFactorMethods = tfa.Methods;
            ShowTwoFactor();
        }
        catch (Exception ex)
        {
            SetLoginStatus(FriendlyError.Of(ex), error: true);
            StartClipboardWatch(); // やり直せるように監視を戻す
        }
        finally { SetLoginBusy(false); }
    }

    private void ShowTwoFactor()
    {
        CredentialsPanel.Visibility = Visibility.Collapsed;
        TwoFactorPanel.Visibility = Visibility.Visible;
        TwoFactorHint.Text = _twoFactorMethods.Contains("totp")
            ? "認証アプリに表示されている 6 桁のコードを入力してください(リカバリーコードも可)。"
            : "VRChat に登録したメールアドレスに届いた 6 桁のコードを入力してください。";
        LoginStatus.Text = "";
        TwoFactorCodeBox.Clear();
        TwoFactorCodeBox.Focus();
    }

    private void BackToLogin_Click(object sender, RoutedEventArgs e)
    {
        StopClipboardWatch();
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        ExternalLoginPanel.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Visible;
        LoginStatus.Text = "";
    }

    private void TwoFactorField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) VerifyButton_Click(sender, e);
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        var code = TwoFactorCodeBox.Text.Trim();
        if (code.Length == 0) return;
        SetLoginBusy(true, "認証中...");
        try
        {
            string method;
            if (_twoFactorMethods.Contains("emailOtp")) method = "emailOtp";
            // 8桁英数字ならリカバリーコード(otp)、それ以外は TOTP
            else if (code.Length == 8 && _twoFactorMethods.Contains("otp") && !code.All(char.IsDigit)) method = "otp";
            else method = "totp";

            var me = await _api.VerifyTwoFactorAsync(method, code);
            TwoFactorPanel.Visibility = Visibility.Collapsed;
            CredentialsPanel.Visibility = Visibility.Visible;
            await EnterMainAsync(me);
        }
        catch (Exception ex) { SetLoginStatus(FriendlyError.Of(ex), error: true); }
        finally { SetLoginBusy(false); }
    }

    private void SetLoginBusy(bool busy, string? status = null)
    {
        LoginButton.IsEnabled = !busy;
        BrowserLoginButton.IsEnabled = !busy;
        ExternalLoginButton.IsEnabled = !busy;
        AuthTokenButton.IsEnabled = !busy;
        VerifyButton.IsEnabled = !busy;
        if (status is not null) SetLoginStatus(status, error: false);
    }

    private void SetLoginStatus(string text, bool error)
    {
        LoginStatus.Text = text;
        LoginStatus.SetResourceReference(ForegroundProperty, error ? "DangerBrush" : "MutedTextBrush");
    }

    /// <summary>設定ウィンドウの「ログアウト」から呼ばれる。</summary>
    private async void Logout()
    {
        await _api.LogoutAsync();
        // 「ログイン状態を保持」中は内蔵ブラウザの Discord / Google 等のログインを残す (次回が楽になる)。
        // オフのときは従来どおりプロファイルごと消す
        if (!_settings.KeepBrowserLogin) BrowserLoginWindow.DeleteBrowserProfile();
        // アカウントに紐づくキャッシュはログイン状態の保持に関わらずセッションと一緒に消す
        AvatarListCache.Invalidate(AvatarListCache.Own);
        AvatarListCache.Invalidate(AvatarListCache.Favorites);
        _favoriteGroups = [];
        _favoriteRecords.Clear();
        ReturnToLogin("ログアウトしました。");
    }

    /// <summary>ログイン成功後にメイン画面へ切り替える。</summary>
    private async Task EnterMainAsync(CurrentUser user)
    {
        StopClipboardWatch();
        _user = user;
        TouchRecentAvatar(user.CurrentAvatar); // 今着ているものは「最近使用」の先頭に載せる
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        UpdateUserHeader();
        if (!_osc.IsListening) StartOsc();
        // ログイン直後はキャッシュを使わず必ず取り直す (直前にアップロードしたアバターを隠さないため)。
        // お気に入りの状態 (右クリックの「お気に入りに追加」で行き先を選ぶのに使う) もここで取れる
        await LoadAvatarsAsync(refresh: true);
    }

    /// <summary>メイン画面を畳んでログイン画面に戻す。セッション切れなどでも使う。</summary>
    private void ReturnToLogin(string message)
    {
        _user = null;
        _allItems.Clear();
        AvatarList.ItemsSource = null;
        CloseSettings(); // セッション切れ等で設定を開いたまま戻るケース
        MainPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        // 途中の画面 (2FA / 外部ブラウザ) が残っていても、必ず入力画面から再開する
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        ExternalLoginPanel.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Visible;
        SetLoginStatus(message, error: false);
    }

    /// <summary>API が 401 を返した = セッションが無効。ログイン画面に戻して理由を伝える。true なら処理済み。</summary>
    private bool HandleSessionExpired(Exception ex)
    {
        if (ex is not VRChatApiException { IsUnauthorized: true }) return false;
        _ = _api.LogoutAsync();
        ReturnToLogin("VRChat のセッションが切れました。もう一度ログインしてください。");
        return true;
    }
}
