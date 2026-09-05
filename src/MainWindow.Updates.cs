using System.Windows;

namespace VRCAvatarChanger;

// 自動アップデート: 起動時の新バージョン確認と、更新ボタンからの適用。
public partial class MainWindow
{
    private UpdateInfo? _update;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    /// <summary>
    /// 最新リリースを確認する。見つかったらツールバーにボタンを出すだけで、勝手には更新しない。
    /// 起動時に一度確認し、そのあとは 1 日ごとに見に行く (開きっぱなしでも更新に気づけるように)。
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (_updateTimer is null)
        {
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
            // 見つかったあとは確認しない (ボタンはもう出ている)
            _updateTimer.Tick += async (_, _) => { if (_update is null) await CheckForUpdateAsync(); };
            _updateTimer.Start();
        }
        _update = await Updater.CheckAsync();
        if (_update is null) return;
        UpdateButtonText.Text = $"v{_update.Version.ToString(3)} に更新";
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        // リリースに自動更新用の zip が添付されていない場合は、通知だけして何もしない
        if (_update.ZipUrl is null)
        {
            SetStatus(StatusKind.Info, $"v{_update.Version.ToString(3)} が公開されていますが、自動更新用のファイルがまだ添付されていません");
            return;
        }
        var notes = string.IsNullOrWhiteSpace(_update.Notes) ? "" : "\n\n" + _update.Notes.Trim();
        if (notes.Length > 400) notes = notes[..400] + "…";
        var ok = MessageBox.Show(this,
            $"バージョン {_update.Version.ToString(3)} に更新しますか?\n更新後、アプリは自動で再起動します。{notes}",
            "VRCAvatarChanger の更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        UpdateButton.IsEnabled = false;
        UpdateButtonText.Text = "更新しています";
        SetStatus(StatusKind.Info, "新しいバージョンをダウンロードしています");
        try
        {
            await Updater.DownloadAndApplyAsync(_update); // 成功したら再起動して戻らない
        }
        catch (Exception ex)
        {
            Log.Error($"更新 (v{_update.Version.ToString(3)}) に失敗", ex);
            UpdateButton.IsEnabled = true;
            UpdateButtonText.Text = $"v{_update.Version.ToString(3)} に更新";
            SetStatus(StatusKind.Error, "更新できませんでした: " + FriendlyError.Of(ex));
        }
    }
}
