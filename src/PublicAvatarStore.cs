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
    }
}
