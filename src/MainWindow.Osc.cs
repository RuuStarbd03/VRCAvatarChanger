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
        if (_user is null || _user.CurrentAvatar == avatarId) return;
        _user.CurrentAvatar = avatarId;
        _user.CurrentAvatarThumbnailImageUrl = null; // 旧アバターのサムネなので破棄
        // 一覧に無いアバターの名前・サムネは UpdateUserHeader 内 (ResolveCurrentAvatarAsync) が API から引く
        UpdateUserHeader();
        OscStatusText.Text = $"OSC 連携中 ({DateTime.Now:HH:mm} にゲーム内の着替えを検知)";
    }
}
