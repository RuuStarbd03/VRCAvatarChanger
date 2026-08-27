using System.Windows;

namespace VRCAvatarChanger;

// OSC: VRChat からの /avatar/change を受けて「現在のアバター」表示を追従させる。
public partial class MainWindow
{
    private System.Windows.Threading.DispatcherTimer? _oscRetry;

    /// <summary>OSC の待ち受けを開始。ポートが取れない場合はユーザーには何も見せず、30 秒ごとに静かに再試行する。</summary>
    private void StartOsc()
    {
        try
        {
            _osc.Start();
            OscStatusText.Text = "OSC 連携中";
            OscStatusText.Visibility = Visibility.Visible;
            _oscRetry?.Stop();
            _oscRetry = null;
        }
        catch
        {
            OscStatusText.Visibility = Visibility.Collapsed;
            if (_oscRetry is null)
            {
                _oscRetry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _oscRetry.Tick += (_, _) => { if (_user is not null && !_osc.IsListening) StartOsc(); };
                _oscRetry.Start();
            }
        }
    }

    private void OnOscAvatarChanged(string avatarId)
    {
        // アプリ発の OSC 着替えに対するゲームからのエコーなら、待っている側に成功を伝える
        var isEcho = avatarId == _oscChangeAckId;
        if (isEcho) _oscChangeAck?.TrySetResult(true);

        if (_user is null || _user.CurrentAvatar == avatarId) return;
        _user.CurrentAvatar = avatarId;
        _user.CurrentAvatarThumbnailImageUrl = null; // 旧アバターのサムネなので破棄
        TouchRecentAvatar(avatarId); // ゲーム内での着替えも「最近使用」に数える
        // 一覧に無いアバターの名前・サムネは UpdateUserHeader 内 (ResolveCurrentAvatarAsync) が API から引く
        UpdateUserHeader();
        // アプリ発のエコーは「ゲーム内で着替えた」わけではないので、検知メッセージは出さない
        if (!isEcho) OscStatusText.Text = $"OSC 連携中 ({DateTime.Now:HH:mm} にゲーム内の着替えを検知)";
    }

    // ---------------- OSC でのローカル着替え ----------------

    private TaskCompletionSource<bool>? _oscChangeAck;
    private string? _oscChangeAckId;

    /// <summary>
    /// VRChat が OSC で繋がっていれば、サーバーを経由せずローカルの /avatar/change で着替える。
    /// (API 方式はサーバー → WebSocket → ゲームの経路でイベントが取りこぼされると反映されないことがある)
    /// ゲームは切り替え時に /avatar/change を送り返してくるので、それをもって成功とみなす。
    /// 確認が取れない場合は false を返し、呼び出し側が API 方式にフォールバックする。
    /// </summary>
    private async Task<bool> TryOscChangeAsync(string avatarId)
    {
        // 同じ ID への変更命令はクライアントに無視されるため、OSC では成否を確認できない (API に任せる)
        if (!_osc.IsGameConnected || _user?.CurrentAvatar == avatarId) return false;
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _oscChangeAckId = avatarId;
        _oscChangeAck = ack;
        try
        {
            if (!_osc.SendAvatarChange(avatarId)) return false;
            return await Task.WhenAny(ack.Task, Task.Delay(2000)) == ack.Task;
        }
        finally
        {
            // ダブルクリック連打などで着替えが重なった場合、後発の待ちを消さないよう自分の分だけ片付ける
            if (ReferenceEquals(_oscChangeAck, ack))
            {
                _oscChangeAckId = null;
                _oscChangeAck = null;
            }
        }
    }
}
