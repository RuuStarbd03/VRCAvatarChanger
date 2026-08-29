using System.Windows;
using System.Windows.Input;

namespace VRCAvatarChanger;

// ホットキーで何をするか。キーの検知そのものは MainWindow.QuickOverlay.cs のキーボードフックが行う。
// プレイ中はメイン画面が見えないので、結果はゲームの上に小さく出す (ToastWindow)。
public partial class MainWindow
{
    /// <summary>今有効な割り当て。設定を変えたら RebuildHotkeys で作り直す。</summary>
    private readonly List<(Hotkey Key, HotkeyAction Action, string? AvatarId, string? Name)> _hotkeys = [];

    private ToastWindow? _toast;

    private void RebuildHotkeys()
    {
        _hotkeys.Clear();
        Add(Hotkey.Parse(_settings.QuickHotkey), HotkeyAction.Overlay, null, null);
        Add(Hotkey.Parse(_settings.PreviousHotkey), HotkeyAction.Previous, null, null);
        Add(Hotkey.Parse(_settings.NextInGroupHotkey), HotkeyAction.NextInGroup, null, null);
        foreach (var binding in _settings.AvatarHotkeys)
            if (VRChatApi.IsValidAvatarId(binding.AvatarId))
                Add(Hotkey.Parse(binding.Key), HotkeyAction.Avatar, binding.AvatarId, binding.Name);

        // 同じキーが二重に割り当たっていたら先に登録したものを優先する (設定画面では弾いているが、手で書き換えられた場合の保険)
        void Add(Hotkey key, HotkeyAction action, string? avatarId, string? name)
        {
            if (!key.IsSet || _hotkeys.Any(h => h.Key == key)) return;
            _hotkeys.Add((key, action, avatarId, name));
        }
    }

    /// <summary>そのキーに (修飾キーを問わず) 割り当てがあるか。押鍵ごとの判定を安く済ませるための足切り。</summary>
    private bool HasHotkeyFor(Key key)
    {
        if (key == Key.None) return false;
        foreach (var hotkey in _hotkeys)
            if (hotkey.Key.Key == key) return true;
        return false;
    }

    private (Hotkey Key, HotkeyAction Action, string? AvatarId, string? Name)? FindHotkey(Hotkey pressed)
    {
        foreach (var hotkey in _hotkeys)
            if (hotkey.Key == pressed) return hotkey;
        return null;
    }

    private void RunHotkey((Hotkey Key, HotkeyAction Action, string? AvatarId, string? Name) hit)
    {
        switch (hit.Action)
        {
            case HotkeyAction.Overlay:
                ToggleQuickOverlay();
                break;
            case HotkeyAction.Previous:
                _ = ChangeToPreviousAsync(toast: true);
                break;
            case HotkeyAction.NextInGroup:
                _ = ChangeToNextInGroupAsync();
                break;
            case HotkeyAction.Avatar when hit.AvatarId is not null:
                _ = ChangeByHotkeyAsync(hit.AvatarId, hit.Name, toast: true);
                break;
        }
    }

    // ---------------- 直前のアバターに戻す ----------------

    /// <summary>1 つ前に着ていたアバター。まだ履歴が無ければ null。</summary>
    private string? PreviousAvatarId()
    {
        var current = _user?.CurrentAvatar;
        return _settings.RecentAvatars.FirstOrDefault(id => id != current && VRChatApi.IsValidAvatarId(id));
    }

    /// <param name="toast">ゲームの上に通知を出すか。メイン画面のボタンから押されたときは要らない</param>
    private async Task ChangeToPreviousAsync(bool toast)
    {
        if (PreviousAvatarId() is not { } id)
        {
            NotifyHotkey("直前のアバターがありません", error: true, toast);
            return;
        }
        await ChangeByHotkeyAsync(id, null, toast);
    }

    // ---------------- グループ内の送り ----------------

    private async Task ChangeToNextInGroupAsync()
    {
        var current = _user?.CurrentAvatar;
        var group = current is null ? null : _groups.GroupOf(current);
        if (group is null || group.AvatarIds.Count < 2)
        {
            NotifyHotkey("今のアバターはグループに入っていません", error: true, toast: true);
            return;
        }
        var index = group.AvatarIds.IndexOf(current!);
        var next = group.AvatarIds[(index + 1) % group.AvatarIds.Count];
        await ChangeByHotkeyAsync(next, null, toast: true);
    }

    // ---------------- 共通 ----------------

    /// <summary>ホットキーからの着替え。一覧に無いアバターでも ID があれば着替えられる。</summary>
    private async Task ChangeByHotkeyAsync(string avatarId, string? fallbackName, bool toast)
    {
        if (_user is null)
        {
            NotifyHotkey("VRChat にログインしていません", error: true, toast);
            return;
        }
        var name = NameOf(avatarId) ?? fallbackName ?? avatarId;
        NotifyHotkey($"{name} に着替えています", error: false, toast);
        var ok = await ChangeAvatarAsync(avatarId, name);
        NotifyHotkey(ok ? $"{name} に着替えました" : $"{name} に着替えられませんでした", error: !ok, toast);
    }

    /// <summary>分かる範囲でアバターの表示名を引く(一覧 → 単体取得のキャッシュ)。</summary>
    private string? NameOf(string avatarId)
    {
        var item = _allItems.FirstOrDefault(a => a.IsAvatar && a.Id == avatarId);
        if (item is not null) return item.Name;
        if (_avatarInfoCache.TryGetValue(avatarId, out var avatar)) return avatar.Name;
        return _settings.AvatarHotkeys.FirstOrDefault(h => h.AvatarId == avatarId)?.Name is { Length: > 0 } n ? n : null;
    }

    /// <summary>ステータス欄に出し、ゲームの上にも短く出す(プレイ中はメイン画面が見えないため)。</summary>
    private void NotifyHotkey(string text, bool error, bool toast)
    {
        SetStatus(error ? StatusKind.Error : StatusKind.Info, text);
        if (!toast || _preview) return;
        try
        {
            _toast ??= new ToastWindow();
            _toast.ShowMessage(text, QuickOverlayAreaPx(), Win32.ScaleOf(_vrchatHwnd), error);
        }
        catch { /* 通知が出せなくても着替えそのものには影響しない */ }
    }

    // ---------------- 設定画面 ----------------

    private void HotkeySettings_Click(object sender, RoutedEventArgs e) => OpenHotkeySettings();

    private void MenuAssignHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) OpenHotkeySettings((item.Id, item.Name));
    }

    private void OpenHotkeySettings((string Id, string Name)? assign = null)
    {
        var window = new HotkeyWindow(_settings, () => { if (!_preview) _settings.Save(); }, assign) { Owner = this };
        window.ShowDialog();
        RebuildHotkeys();
    }
}
