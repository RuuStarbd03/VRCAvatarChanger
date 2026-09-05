using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCAvatarChanger;

public sealed class PublicAvatarEntry
{
    public Avatar Avatar { get; set; } = new();
    public DateTimeOffset AddedAt { get; set; }
    /// <summary>アバター情報を最後に API から取り直した日時。null = 追加時のまま。</summary>
    public DateTimeOffset? RefreshedAt { get; set; }
    /// <summary>ユーザーが自由に付けるタグ(絞り込み用)。</summary>
    public List<string> Tags { get; set; } = [];

    // ---- 使えなくなった (削除・非公開化) の検出 ----
    // 1 回の失敗で断定すると VRChat 側の一時的な不調で誤検出するので、
    // 時間を空けて 2 回続けて取れなかったときに「使えない」と確定する。
    // 確定しても自動では外さない (非公開が一時的なこともある)。外すのは利用者の操作で。

    /// <summary>取り直しに続けて失敗した回数。成功したら 0 に戻す。</summary>
    public int UnavailableStrikes { get; set; }
    /// <summary>最後に取り直しに失敗した日時。</summary>
    public DateTimeOffset? LastFailedAt { get; set; }
    /// <summary>「使えない」と確定した日時。null = 使える (または未確定)。</summary>
    public DateTimeOffset? UnavailableSince { get; set; }
    /// <summary>使えない理由。"deleted" (見つからない) / "private" (非公開になった)。</summary>
    public string? UnavailableReason { get; set; }

    public bool IsUnavailable => UnavailableSince is not null;
}

/// <summary>
/// アプリ独自の「パブリックアバター」リスト。VRChat のお気に入り上限とは無関係に、いくつでも登録できる。
/// アバター情報はローカルにキャッシュし、起動時は API を叩かずに表示する。
/// %AppData%\VRCAvatarChanger\public_avatars.json に平文で保存(機密情報は含まない)。
/// </summary>
public sealed class PublicAvatarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<PublicAvatarEntry> _entries = [];

    public IReadOnlyList<PublicAvatarEntry> Entries => _entries;

    private static string PathOf() => AppPaths.In("public_avatars.json");

    public static PublicAvatarStore Load()
    {
        var store = new PublicAvatarStore();
        var list = JsonFile.Load<List<PublicAvatarEntry>>(PathOf(), JsonOptions);
        if (list is not null)
            store._entries.AddRange(list.Where(e => VRChatApi.IsValidAvatarId(e.Avatar.Id)));
        return store;
    }

    public void Save() => JsonFile.Save(PathOf(), _entries, JsonOptions);

    public bool Contains(string avatarId) => _entries.Any(e => e.Avatar.Id == avatarId);

    /// <summary>追加。すでにあれば情報だけ更新して false を返す。</summary>
    public bool Add(Avatar avatar)
    {
        var existing = _entries.FirstOrDefault(e => e.Avatar.Id == avatar.Id);
        if (existing is not null) { existing.Avatar = avatar; existing.RefreshedAt = DateTimeOffset.Now; Save(); return false; }
        _entries.Add(new PublicAvatarEntry { Avatar = avatar, AddedAt = DateTimeOffset.Now, RefreshedAt = DateTimeOffset.Now });
        Save();
        return true;
    }

    public bool Remove(string avatarId)
    {
        var n = _entries.RemoveAll(e => e.Avatar.Id == avatarId);
        if (n > 0) Save();
        return n > 0;
    }

    public void Update(Avatar avatar)
    {
        var existing = _entries.FirstOrDefault(e => e.Avatar.Id == avatar.Id);
        if (existing is null) return;
        existing.Avatar = avatar;
        existing.RefreshedAt = DateTimeOffset.Now;
        // 取れた = 使える。以前の失敗は帳消しにする (非公開が戻った場合など)
        existing.UnavailableStrikes = 0;
        existing.LastFailedAt = null;
        existing.UnavailableSince = null;
        existing.UnavailableReason = null;
    }

    /// <summary>この回数続けて取れなかったら「使えない」と確定する。</summary>
    public const int StrikesToConfirm = 2;

    /// <summary>失敗と失敗の間に最低これだけ空ける (同じ不調の中で 2 回数えないため)。</summary>
    public static readonly TimeSpan StrikeInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// 取り直しで「見つからない / 非公開」が返ったときに呼ぶ。回数を数え、規定回数に達したら確定する。
    /// </summary>
    /// <returns>この呼び出しで新たに確定したら true。</returns>
    public bool MarkFailed(string avatarId, string reason)
    {
        var e = _entries.FirstOrDefault(x => x.Avatar.Id == avatarId);
        if (e is null) return false;
        var now = DateTimeOffset.Now;
        e.LastFailedAt = now;
        e.UnavailableReason = reason;
        if (e.IsUnavailable) return false; // すでに確定済み
        e.UnavailableStrikes++;
        if (e.UnavailableStrikes < StrikesToConfirm) return false;
        e.UnavailableSince = now;
        return true;
    }

    /// <summary>確定した「使えない」項目。</summary>
    public IEnumerable<PublicAvatarEntry> Unavailable => _entries.Where(e => e.IsUnavailable);

    /// <summary>確定した「使えない」項目をまとめて外す。</summary>
    /// <returns>外した件数。</returns>
    public int RemoveUnavailable()
    {
        var n = _entries.RemoveAll(e => e.IsUnavailable);
        if (n > 0) Save();
        return n;
    }
}
