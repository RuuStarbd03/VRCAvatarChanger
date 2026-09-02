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

    /// <summary>この時間内に取り直した情報はそのまま使う(登録数が多いと 1 件 1 リクエストで重いため)。</summary>
    private static readonly TimeSpan PublicEntryFreshFor = TimeSpan.FromHours(6);

    /// <summary>
    /// 取り直しの対象か。前回うまく取れてから 6 時間、失敗してから 1 時間は空ける
    /// (失敗の直後に何度も叩いても同じ結果で、「使えない」の確定も時間を空けた 2 回目で行うため)。
    /// </summary>
    private static bool IsStaleEntry(PublicAvatarEntry e, DateTimeOffset now)
    {
        if (e.LastFailedAt is { } failed && now - failed < PublicAvatarStore.StrikeInterval) return false;
        return e.RefreshedAt is not { } at || now - at > PublicEntryFreshFor;
    }

    /// <summary>
    /// パブリックリストのアバター情報を API から取り直す。「再読み込み」のときだけ呼ぶ。
    /// 1 件につき 1 リクエストなので、しばらく前に取ったものだけを対象にする。
    /// 「見つからない」「非公開」が返ったものは失敗として数え、時間を空けて 2 回続いたら「使えない」と確定する
    /// (通信断やレート制限などそれ以外の失敗は数えない。VRChat 側の不調を削除と取り違えないため)。
    /// </summary>
    /// <returns>実際に取り直した件数と、この回で新たに「使えない」と確定した件数。</returns>
    private async Task<(int Refreshed, int NewlyUnavailable)> RefreshPublicEntriesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var stale = _public.Entries.Where(e => IsStaleEntry(e, now)).ToList();
        if (stale.Count == 0) return (0, 0);

        var newlyUnavailable = 0;
        // 件数が多くてもレート制限にかからないよう、同時 2 本まで
        using var gate = new SemaphoreSlim(2);
        var tasks = stale.Select(async e =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var av = await _api.GetAvatarAsync(e.Avatar.Id, ct);
                // 取れても非公開になっていれば着替えられない (自分のアバターなら非公開でも着られる)
                if (av.ReleaseStatus != "public" && av.AuthorId != _user?.Id)
                {
                    if (_public.MarkFailed(e.Avatar.Id, "private")) Interlocked.Increment(ref newlyUnavailable);
                }
                else _public.Update(av);
            }
            catch (OperationCanceledException) { throw; }
            catch (VRChatApiException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                // 404 は削除・非公開のどちらでも返る (他人の非公開アバターは「無いもの」として扱われる)
                var reason = ex.Status == System.Net.HttpStatusCode.Forbidden ? "private" : "deleted";
                if (_public.MarkFailed(e.Avatar.Id, reason)) Interlocked.Increment(ref newlyUnavailable);
            }
            catch { /* 通信断・レート制限など: 判断できないので前回の状態のまま */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        _public.Save();
        return (stale.Count, newlyUnavailable);
    }

    /// <summary>「使えない」と確定した項目の見た目 (バッジなど) を、リストの項目に写す。</summary>
    private static AvatarItem PublicItemOf(PublicAvatarEntry e, IReadOnlyList<string> tags)
        => new(e.Avatar)
        {
            AddedAt = e.AddedAt,
            Tags = tags,
            IsUnavailable = e.IsUnavailable,
            UnavailableSince = e.UnavailableSince,
            UnavailableReason = e.UnavailableReason,
        };

    /// <summary>
    /// 一覧から着替えるときの入口。使えなくなったアバターなら、そのまま失敗させずに先に確かめる
    /// (外すか、それでも試すか)。
    /// </summary>
    private async Task ChangeFromListAsync(AvatarItem item)
    {
        if (item.IsUnavailable && IsPublicTab)
        {
            var r = MessageBox.Show(this,
                $"{item.Name} は使えなくなっています。\n{item.UnavailableText}\n\n" +
                "パブリックから外しますか?\n(「いいえ」でそのまま着替えを試します)",
                "VRCAvatarChanger", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes)
            {
                _public.Remove(item.Id);
                SetStatus(StatusKind.Info, $"{item.Name} をパブリックから外しました");
                await LoadAvatarsAsync();
                return;
            }
        }
        await ChangeAvatarAsync(item.Id, item.Name);
    }

    private async void MenuRemoveUnavailable_Click(object sender, RoutedEventArgs e)
    {
        var targets = _public.Unavailable.ToList();
        if (targets.Count == 0) return;
        var names = string.Join("\n", targets.Take(8).Select(t => "・" + t.Avatar.Name));
        if (targets.Count > 8) names += $"\n・ほか {targets.Count - 8} 件";
        var r = MessageBox.Show(this,
            $"使えなくなった {targets.Count} 件をパブリックから外します。\n\n{names}\n\n" +
            "外したあとは、もう一度 ID か URL で追加すれば戻せます。",
            "VRCAvatarChanger", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        var n = _public.RemoveUnavailable();
        SetStatus(StatusKind.Info, $"使えなくなった {n} 件をパブリックから外しました");
        await LoadAvatarsAsync();
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
