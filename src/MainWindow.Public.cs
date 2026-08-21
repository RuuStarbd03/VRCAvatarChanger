using System.Windows;
using System.Windows.Input;

namespace VRCAvatarChanger;

// パブリックリスト: アプリ独自の「パブリックアバター」登録と再取得。
public partial class MainWindow
{
    /// <summary>入力から avtr_ ID を取り出す。URL (https://vrchat.com/home/avatar/avtr_...) も可。</summary>
    private static string? ExtractAvatarId(string input)
    {
        var s = input.Trim();
        var idx = s.IndexOf("avtr_", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[idx..].Split('?', '/', '#', ' ')[0];
        return VRChatApi.IsValidAvatarId(s) ? s : null;
    }

    private void PublicIdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PublicAdd_Click(sender, e);
    }

    private async void PublicAdd_Click(object sender, RoutedEventArgs e)
    {
        var id = ExtractAvatarId(PublicIdBox.Text);
        if (id is null)
        {
            SetStatus(StatusKind.Error, "avtr_ で始まるアバター ID か、アバターページの URL を入力してください。");
            return;
        }
        PublicAddButton.IsEnabled = false;
        try
        {
            SetStatus(StatusKind.Info, "アバター情報を取得しています");
            var av = await _api.GetAvatarAsync(id);
            if (await TryAddPublicAsync(av)) PublicIdBox.Clear();
        }
        catch (Exception ex) { SetStatus(StatusKind.Error, "アバター情報を取得できませんでした: " + FriendlyError.Of(ex)); }
        finally { PublicAddButton.IsEnabled = true; }
    }

    /// <summary>パブリックリストに追加。他人の非公開アバターは着替えられないので拒否する。</summary>
    private async Task<bool> TryAddPublicAsync(Avatar av)
    {
        if (av.ReleaseStatus != "public")
        {
            SetStatus(StatusKind.Error, $"{av.Name} は非公開アバターのため追加できません");
            return false;
        }
        var added = _public.Add(av);
        SetStatus(added ? StatusKind.Success : StatusKind.Info, added ? $"{av.Name} をパブリックに追加しました" : $"{av.Name} はすでにパブリックにあります");
        if (IsPublicTab) await LoadAvatarsAsync();
        return added;
    }

    private async Task RefreshPublicEntriesAsync(CancellationToken ct)
    {
        // 件数が多くてもレート制限にかからないよう、同時 2 本まで
        using var gate = new SemaphoreSlim(2);
        var tasks = _public.Entries.ToList().Select(async e =>
        {
            await gate.WaitAsync(ct);
            try { _public.Update(await _api.GetAvatarAsync(e.Avatar.Id, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* 削除された・非公開になったアバターはそのまま残す */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        _public.Save();
    }

    private async void MenuAddPublic_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) await TryAddPublicAsync(item.Avatar);
    }

    private async void MenuRemovePublic_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem { IsAvatar: true } item) return;
        _public.Remove(item.Id);
        SetStatus(StatusKind.Info, $"{item.Name} をパブリックから削除しました");
        await LoadAvatarsAsync();
    }
}
