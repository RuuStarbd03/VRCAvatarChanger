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
        var win = new BrowserLoginWindow(_api) { Owner = this };
        var ok = win.ShowDialog() == true && win.Result is not null;
        if (ok) await EnterMainAsync(win.Result!);
        else SetLoginStatus("ブラウザでのログインを中止しました", error: false);
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
        TwoFactorPanel.Visibility = Visibility.Collapsed;
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
        BrowserLoginWindow.DeleteBrowserProfile();
        // アカウントに紐づくキャッシュもセッションと一緒に消す
        AvatarListCache.Invalidate(AvatarListCache.Own);
        AvatarListCache.Invalidate(AvatarListCache.Favorites);
        _favoriteGroups = [];
        _favoriteRecords.Clear();
        ReturnToLogin("ログアウトしました。");
    }

    /// <summary>ログイン成功後にメイン画面へ切り替える。</summary>
    private async Task EnterMainAsync(CurrentUser user)
    {
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
