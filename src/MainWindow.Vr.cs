using System.Windows;
using System.Windows.Controls;

namespace VRCAvatarChanger;

// VR (SteamVR): 手の甲のボタンからのアバター選択。SteamVR とのやりとりは VrOverlay.cs、見た目は VrPanel.cs。
// ここは設定との橋渡しと、VR パネルに並べる一覧の出どころ (IVrAvatarSource)。
// PC のクイック着替えとは別物で、メイン画面で開いているタブに関係なく、VR パネルで選んだタブの一覧を出す。
// 絞り込み・並べ替え・グループ化はメイン画面と同じ関数を使う (見え方をそろえる)。
public partial class MainWindow : IVrAvatarSource
{
    private VrOverlay? _vr;

    /// <summary>
    /// VR パネル用に作った項目 (タブ → アバター ID → 項目)。サムネイルは項目が持つので、
    /// 開くたびに作り直さず使い回して、一度読んだ画像を持ち越す。
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, AvatarItem>> _vrItems = [];

    /// <summary>VR パネルから VRChat へ取りに行った一覧 (一覧キャッシュが空のとき用。0 件でも「取得済み」と分かるように持つ)。</summary>
    private readonly Dictionary<string, List<Avatar>> _vrFetched = [];

    /// <summary>ctor から一度だけ呼ぶ。設定が ON なら SteamVR の見張りを始める (プレビューでは始めない)。</summary>
    private void InitVr()
    {
        _vr = new VrOverlay(_settings, this);
        if (_settings.VrOverlay && !_preview) _vr.Start();
        Closed += (_, _) => _vr?.Dispose();
    }

    /// <summary>メイン画面で今開いているタブ (VR パネルのタブと同じ呼び名)。</summary>
    private string MainTab => IsPublicTab ? AvatarTab.Public : IsFavoritesTab ? AvatarTab.Favorites : AvatarTab.Own;

    // ---------------- VR パネルに並べる一覧 ----------------

    IReadOnlyList<AvatarItem>? IVrAvatarSource.ItemsOf(string tab)
    {
        // メイン画面が同じタブを出していれば、その項目をそのまま使う (サムネイルも共有できる)
        if (tab == MainTab && _allItems.Count > 0) return _allItems;
        if (tab == AvatarTab.Public) return VrItems(tab, _public.Entries.Select(e => (e.Avatar, (PublicAvatarEntry?)e)));
        if (_user is null) return null;
        var avatars = AvatarListCache.Load(tab, _user.Id)?.Avatars ?? _vrFetched.GetValueOrDefault(tab);
        return avatars is null ? null : VrItems(tab, avatars.Select(a => (a, (PublicAvatarEntry?)null)));
    }

    /// <summary>アバターの並びから項目を作る。前に作った項目があり、中身が同じならそれを使い回す。</summary>
    private List<AvatarItem> VrItems(string tab, IEnumerable<(Avatar Avatar, PublicAvatarEntry? Entry)> source)
    {
        if (!_vrItems.TryGetValue(tab, out var byId)) _vrItems[tab] = byId = new(StringComparer.Ordinal);
        var favorites = tab == AvatarTab.Favorites;
        var list = new List<AvatarItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (avatar, entry) in source)
        {
            if (!seen.Add(avatar.Id)) continue;
            if (!byId.TryGetValue(avatar.Id, out var item) || !ReferenceEquals(item.Avatar, avatar)
                || item.IsUnavailable != (entry?.IsUnavailable ?? false))
                byId[avatar.Id] = item = entry is not null ? PublicItemOf(entry, []) : NewItem(avatar, favorites);
            // タグは PC 側で付け替えられていることがあるので毎回合わせる (お気に入りはグループ表示を優先して付けない)
            IReadOnlyList<string> tags = favorites ? [] : _tags.TagsOf(avatar.Id);
            if (!ReferenceEquals(item.Tags, tags) && !item.Tags.SequenceEqual(tags)) item.Tags = tags;
            list.Add(item);
        }
        // 一覧から消えたものは手放す (サムネイルを抱えたまま残さない)
        if (byId.Count > seen.Count)
            foreach (var id in byId.Keys.Where(id => !seen.Contains(id)).ToList()) byId.Remove(id);
        return list;
    }

    async Task IVrAvatarSource.LoadAsync(string tab)
    {
        if (tab == AvatarTab.Public) return; // パブリックは手元のリストなので取りに行くものはない
        if (_user is null) throw new InvalidOperationException("ログインしていません。PC の AvatarChanger でログインしてください。");
        var avatars = tab == AvatarTab.Favorites ? await _api.GetFavoriteAvatarsAsync() : await _api.GetOwnAvatarsAsync();
        AvatarListCache.Save(tab, _user.Id, avatars);
        _vrFetched[tab] = avatars;
    }

    List<string> IVrAvatarSource.FilterValuesOf(IReadOnlyList<AvatarItem> items, string tab)
        => FilterValuesOf(items, tab == AvatarTab.Favorites);

    List<AvatarItem> IVrAvatarSource.Arrange(IReadOnlyList<AvatarItem> items, VrView view)
    {
        var filtered = FilterBy(items, view.Filter, view.Tab == AvatarTab.Favorites);
        var sorted = ApplySort(filtered, view.SortKey, _settings.RecentAvatars).ToList();
        var tiles = view.OpenGroup is { } group ? MembersOf(sorted, group)
            : view.GroupView ? GroupIntoTiles(sorted, view.SortKey)
            : sorted;
        var current = _user?.CurrentAvatar;
        foreach (var tile in tiles) tile.MarkCurrent(current);
        return tiles;
    }

    bool IVrAvatarSource.GroupExists(AvatarGroup group) => _groups.Groups.Contains(group);

    Task<bool> IVrAvatarSource.ChangeAsync(AvatarItem item) => ChangeAvatarAsync(item.Id, item.Name);

    void IVrAvatarSource.RequestThumbnail(AvatarItem item) => RequestThumbnail(item);

    void IVrAvatarSource.SaveSettings()
    {
        SaveSettings();
        UpdateVrOptions(); // VR の中でボタンを動かしたら、設定画面の「位置をリセット」を押せるように
    }

    /// <summary>設定画面を開いたときに、今の値を表示に写す。</summary>
    private void ShowVrSettings()
    {
        VrToggle.IsChecked = _settings.VrOverlay;
        (_settings.VrHand == "right" ? VrHandRight : VrHandLeft).IsChecked = true;
        UpdateVrOptions();
    }

    private void UpdateVrOptions()
    {
        VrOptionsPanel.IsEnabled = _settings.VrOverlay;
        VrResetPositionButton.IsEnabled = _settings.VrButtonPose is not null;
    }

    private void VrToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var enabled = VrToggle.IsChecked == true;
        if (_settings.VrOverlay == enabled) return;
        _settings.VrOverlay = enabled;
        SaveSettings();
        UpdateVrOptions();
        if (enabled && !_preview) _vr?.Start();
        else _vr?.Stop();
        SetStatus(StatusKind.Info, enabled
            ? "VR のボタン: オン"
            : "VR のボタン: オフ");
    }

    private void VrHand_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var hand = (sender as RadioButton)?.Tag as string ?? "left";
        if (_settings.VrHand == hand) return; // 設定画面を開いたときの初期化では何もしない
        _settings.VrHand = hand;
        _settings.VrButtonPose = null; // 動かした位置は前の手から見たものなので、新しい手では手の甲から
        SaveSettings(); // 付け替えは次のフレームで VrOverlay が拾う
        UpdateVrOptions();
    }

    private void VrResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _settings.VrButtonPose = null; // 次のフレームで VrOverlay が手の甲に貼り直す
        SaveSettings();
        UpdateVrOptions();
        SetStatus(StatusKind.Info, "VR のボタンを手の甲に戻しました");
    }
}
