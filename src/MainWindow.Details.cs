using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VRCAvatarChanger;

/// <summary>詳細画面の 1 行 (項目名と値)。ID などは等幅で出す。</summary>
public sealed record DetailRow(string Label, string Value, bool Mono = false)
{
    public FontFamily FontFamily => Mono ? MonoFont : UiFont;

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    private static readonly FontFamily UiFont = (FontFamily)Application.Current.FindResource("UiFont");
}

// アバターの詳細 (アプリ内オーバーレイ)。
// 一覧のタイルには載せきれない情報 (説明文、対応プラットフォーム、パフォーマンス、日付、
// 使えなくなった理由と確認日時など) をまとめて見せる。似た衣装違いの区別や、着替える前の確認に使う。
public partial class MainWindow
{
    private AvatarItem? _detailItem;
    private bool _detailOpen;
    private CancellationTokenSource? _detailCts;

    /// <summary>詳細画面で出す画像の展開幅。カードの画像枠 (336 DIP) に高 DPI でも足りる程度</summary>
    private const int DetailImageWidth = 512;

    private void MenuDetails_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) OpenDetail(item);
    }

    private void OpenDetail(AvatarItem item)
    {
        _detailItem = item;
        var a = item.Avatar;

        DetailName.Text = a.Name;
        DetailAuthor.Text = a.AuthorName;
        DetailId.Text = a.Id;
        DetailStatus.Text = a.ReleaseStatus switch
        {
            "public" => "公開",
            "private" => a.AuthorId == _user?.Id ? "非公開 (自分のアバター)" : "非公開",
            _ => "",
        };
        DetailDescription.Text = string.IsNullOrWhiteSpace(a.Description) ? "(説明はありません)" : a.Description.Trim();
        DetailCurrentBadge.Visibility = a.Id == _user?.CurrentAvatar ? Visibility.Visible : Visibility.Collapsed;

        // 使えなくなったもの: 理由と確認日時。外す操作はパブリックタブでだけ意味がある
        var unavailable = item.IsUnavailable;
        DetailUnavailable.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        DetailUnavailableText.Text = item.UnavailableText;
        DetailRemoveButton.Visibility = unavailable && IsPublicTab ? Visibility.Visible : Visibility.Collapsed;
        DetailChangeButton.Content = unavailable ? "それでも着替えを試す" : "このアバターに着替える";

        DetailRows.ItemsSource = BuildDetailRows(item);

        // 画像: 手元のサムネイルを先に出し、大きいものが取れたら差し替える
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        DetailImage.Source = item.Thumbnail;
        _ = LoadDetailImageAsync(item, _detailCts.Token);
        // ダウンロードサイズは 1 件 1 リクエストなので、開いたときに 1 回だけ引く
        _ = LoadDetailSizeAsync(item, _detailCts.Token);

        if (DetailOverlay.Visibility != Visibility.Visible)
        {
            DetailOverlay.Opacity = 0;
            DetailCardScale.ScaleX = DetailCardScale.ScaleY = 0.96;
            DetailOverlay.Visibility = Visibility.Visible;
        }
        AnimateDetail(open: true);
        DetailChangeButton.Focus();
    }

    private List<DetailRow> BuildDetailRows(AvatarItem item)
    {
        var a = item.Avatar;
        var rows = new List<DetailRow>();

        // 対応プラットフォーム: アップロード済みのアセットから。VRChat の画面の言い方に合わせる
        var platforms = (a.UnityPackages ?? [])
            .Select(p => p.Platform switch
            {
                "standalonewindows" => "PC",
                "android" => "Android (Quest)",
                "ios" => "iOS",
                null or "" => null,
                var other => other,
            })
            .OfType<string>().Distinct().ToList();
        rows.Add(new("対応プラットフォーム", platforms.Count > 0 ? string.Join(" / ", platforms) : "不明"));

        // パフォーマンスランク (PC / Android)。判定が無ければその旨
        var pc = PerformanceLabel(a.Performance?.Windows);
        var android = PerformanceLabel(a.Performance?.Android);
        rows.Add(new("パフォーマンス", (pc, android) switch
        {
            (null, null) => "未判定",
            (_, null) => $"PC: {pc}",
            (null, _) => $"Android: {android}",
            _ => $"PC: {pc} / Android: {android}",
        }));

        rows.Add(new("ダウンロードサイズ", SizeText(a)));
        rows.Add(new("作成日", Date(a.CreatedAt)));
        rows.Add(new("更新日", Date(a.UpdatedAt)));
        if (item.AddedAt is { } added && IsPublicTab) rows.Add(new("パブリックに追加", Date(added)));
        if (!string.IsNullOrEmpty(a.FavoriteGroup)) rows.Add(new("お気に入りグループ", a.FavoriteGroup));
        if (item.Tags.Count > 0) rows.Add(new("タグ", string.Join(", ", item.Tags)));
        if (_groups.GroupOf(a.Id) is { } g) rows.Add(new("グループ", $"{g.Name} ({g.AvatarIds.Count} 体)"));
        var keys = _settings.AvatarHotkeys.Where(h => h.AvatarId == a.Id && !string.IsNullOrEmpty(h.Key))
            .Select(h => Hotkey.Parse(h.Key).Display).ToList();
        if (keys.Count > 0) rows.Add(new("ホットキー", string.Join(", ", keys)));
        return rows;

        static string Date(DateTimeOffset? d) => d is { } v ? v.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "不明";
    }

    /// <summary>ダウンロードサイズの行の中身。まだ引いていなければ「取得中」(裏で引いて差し替える)。</summary>
    private static string SizeText(Avatar a)
    {
        if (a.WindowsAssetRef is not { } r) return "不明";
        return AssetSizeCache.TryGet(r.FileId, r.Version, out var b) ? $"{AssetSizeCache.Format(b)} (PC)" : "取得中...";
    }

    private async Task LoadDetailSizeAsync(AvatarItem item, CancellationToken ct)
    {
        if (_preview || item.Avatar.WindowsAssetRef is not { } r) return;
        if (AssetSizeCache.TryGet(r.FileId, r.Version, out _)) return;
        try
        {
            var size = await _api.GetAssetSizeAsync(r.FileId, r.Version, ct);
            if (size is { } s) { AssetSizeCache.Set(r.FileId, r.Version, s); AssetSizeCache.Flush(); }
            if (ct.IsCancellationRequested || !ReferenceEquals(_detailItem, item)) return;
            // 取れなくても「取得中」のままにしない
            var rows = (DetailRows.ItemsSource as List<DetailRow>)?.ToList();
            if (rows is null) return;
            var at = rows.FindIndex(x => x.Label == "ダウンロードサイズ");
            if (at >= 0) rows[at] = rows[at] with { Value = size is { } b ? $"{AssetSizeCache.Format(b)} (PC)" : "不明" };
            DetailRows.ItemsSource = rows;
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadDetailImageAsync(AvatarItem item, CancellationToken ct)
    {
        // 一覧のサムネイル (thumbnailImageUrl) は小さいので、元画像 (imageUrl) があればそちらを使う
        var url = item.Avatar.ImageUrl ?? item.ThumbnailUrl;
        if (string.IsNullOrEmpty(url)) return;
        var img = await GetImageAsync(url, DetailImageWidth, ct);
        if (img is null || ct.IsCancellationRequested || !ReferenceEquals(_detailItem, item)) return;
        DetailImage.Source = img;
    }

    private void CloseDetail()
    {
        if (DetailOverlay.Visibility != Visibility.Visible) return;
        _detailCts?.Cancel();
        AnimateDetail(open: false);
        // 開いた元の行に操作を戻す
        if (AvatarList.SelectedItem is not null) AvatarList.Focus();
    }

    /// <summary>設定と同じ開閉 (フェード + 拡縮)。閉じ切ったら Collapsed にして、大きい画像も手放す。</summary>
    private void AnimateDetail(bool open)
    {
        _detailOpen = open;
        var dur = TimeSpan.FromMilliseconds(open ? 180 : 130);
        var fade = new DoubleAnimation(open ? 1 : 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (!open) fade.Completed += (_, _) =>
        {
            if (_detailOpen) return;
            DetailOverlay.Visibility = Visibility.Collapsed;
            DetailImage.Source = null; // 512px の画像は一覧のものより大きいので、閉じたら持たない
            _detailItem = null;
        };
        DetailOverlay.BeginAnimation(OpacityProperty, fade);

        var scale = new DoubleAnimation(open ? 1 : 0.96, dur)
        {
            EasingFunction = open
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        DetailCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        DetailCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e) => CloseDetail();

    private void DetailBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseDetail();

    private async void DetailChange_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not { } item) return;
        CloseDetail();
        await ChangeFromListAsync(item);
    }

    private async void DetailRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not { } item) return;
        CloseDetail();
        _public.Remove(item.Id);
        SetStatus(StatusKind.Info, $"{item.Name} をパブリックから外しました");
        await LoadAvatarsAsync();
    }

    private void DetailCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not { } item) return;
        try { Clipboard.SetText(item.Id); SetStatus(StatusKind.Info, "アバター ID をコピーしました"); } catch { }
    }

    private void DetailOpenWeb_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not { } item || !VRChatApi.IsValidAvatarId(item.Id)) return;
        var url = "https://vrchat.com/home/avatar/" + item.Id;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { SetStatus(StatusKind.Error, "ブラウザを開けませんでした: " + url); }
    }
}
