using System.Net;
using System.Windows;
using System.Windows.Controls;

namespace VRCAvatarChanger;

// お気に入り: VRChat 側のお気に入りへの登録・解除。
// どのグループに何体入っているかを出すため、グループ一覧と登録レコードをローカルに持っておく
// (お気に入りから外すにはアバター ID ではなく登録 ID (fvrt_...) が要るので、その対応表も兼ねる)。
public partial class MainWindow
{
    private List<FavoriteGroup> _favoriteGroups = [];
    private Dictionary<string, Favorite> _favoriteRecords = new(StringComparer.Ordinal); // アバター ID → 登録レコード

    private bool IsFavorited(string avatarId) => _favoriteRecords.ContainsKey(avatarId);

    /// <summary>
    /// お気に入りのグループ一覧と登録レコードを取り直す。ログイン直後と「再読み込み」で呼ぶ。
    /// 失敗しても黙って諦める(右クリックメニューにお気に入りの項目が出ないだけ)。
    /// </summary>
    private async Task RefreshFavoriteStateAsync(CancellationToken ct = default)
    {
        if (_preview) return;
        try
        {
            var groups = await _api.GetFavoriteGroupsAsync(ct);
            var records = await _api.GetFavoriteRecordsAsync(ct);
            _favoriteGroups = groups;
            _favoriteRecords = records
                .Where(r => VRChatApi.IsValidAvatarId(r.FavoriteId))
                .GroupBy(r => r.FavoriteId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch { /* 次の再読み込みでまた試す */ }
    }

    /// <summary>右クリックメニューのお気に入り項目を、選択中のアバターの状態に合わせて組み直す。</summary>
    private void BuildFavoriteMenu(AvatarItem item)
    {
        var usable = item.IsAvatar && _user is not null && !_preview;
        var favorited = usable && IsFavorited(item.Id);
        MenuRemoveFavorite.Visibility = favorited ? Visibility.Visible : Visibility.Collapsed;
        MenuAddFavorite.Visibility = usable && !favorited ? Visibility.Visible : Visibility.Collapsed;
        if (MenuAddFavorite.Visibility != Visibility.Visible) return;

        // 追加先のグループを選ばせる。グループが取れていない場合は理由を出して押させない
        MenuAddFavorite.Items.Clear();
        if (_favoriteGroups.Count == 0)
        {
            MenuAddFavorite.Items.Add(new MenuItem { Header = "グループを取得できていません(再読み込みしてください)", IsEnabled = false });
            return;
        }
        foreach (var group in _favoriteGroups)
        {
            var count = _favoriteRecords.Values.Count(r => r.Tags.Contains(group.Name, StringComparer.Ordinal));
            var entry = new MenuItem { Header = $"{VRChatApi.FriendlyGroupName(group)} ({count} 体)", Tag = group };
            entry.Click += async (_, _) => await AddFavoriteAsync(item, group);
            MenuAddFavorite.Items.Add(entry);
        }
    }

    private async Task AddFavoriteAsync(AvatarItem item, FavoriteGroup group)
    {
        var groupName = VRChatApi.FriendlyGroupName(group);
        SetStatus(StatusKind.Info, $"{item.Name} を「{groupName}」に登録しています");
        try
        {
            _favoriteRecords[item.Id] = await _api.AddFavoriteAsync(item.Id, group.Name);
            AvatarListCache.Invalidate(AvatarListCache.Favorites); // 次に開いたときは取り直す
            SetStatus(StatusKind.Success, $"{item.Name} をお気に入り「{groupName}」に追加しました");
            if (SourceFavorites.IsChecked == true) await LoadAvatarsAsync();
        }
        catch (Exception ex)
        {
            if (!HandleSessionExpired(ex)) SetStatus(StatusKind.Error, "お気に入りに追加できませんでした: " + FriendlyError.Of(ex));
        }
    }

    private async void MenuRemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem { IsAvatar: true } item) return;
        if (!_favoriteRecords.TryGetValue(item.Id, out var record)) return;
        SetStatus(StatusKind.Info, $"{item.Name} をお気に入りから外しています");
        try
        {
            await _api.RemoveFavoriteAsync(record.Id);
            SetStatus(StatusKind.Success, $"{item.Name} をお気に入りから外しました");
        }
        catch (VRChatApiException gone) when (gone.Status == HttpStatusCode.NotFound)
        {
            // ゲーム内や Web 側で先に外されていた。こちらの記録を合わせるだけでよい
            SetStatus(StatusKind.Info, $"{item.Name} はすでにお気に入りから外れていました");
        }
        catch (Exception ex)
        {
            if (!HandleSessionExpired(ex)) SetStatus(StatusKind.Error, "お気に入りから外せませんでした: " + FriendlyError.Of(ex));
            return;
        }
        _favoriteRecords.Remove(item.Id);
        AvatarListCache.Invalidate(AvatarListCache.Favorites);
        // お気に入りタブなら、取り直さずにその場で一覧から消す
        if (SourceFavorites.IsChecked == true)
        {
            _allItems.RemoveAll(a => a.Id == item.Id);
            BuildFilterChips();
            ApplyFilter();
            UpdateEmptyState(AvatarList.Items.Count);
        }
    }
}
