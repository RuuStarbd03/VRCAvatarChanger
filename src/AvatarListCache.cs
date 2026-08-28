using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCAvatarChanger;

/// <summary>キャッシュした一覧。取得日時は「いつ時点のものか」を利用者に伝えるために持つ。</summary>
public sealed class CachedAvatarList
{
    public string UserId { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }
    public List<Avatar> Avatars { get; set; } = [];
}

/// <summary>
/// 「自分のアバター」「お気に入り」一覧のローカルキャッシュ
/// (%AppData%\VRCAvatarChanger\cache\list-own.json / list-favorites.json)。
///
/// 起動直後は前回の一覧をすぐ出して、その裏で最新を取りに行く。
/// 通信できない・VRChat 側が落ちているときも、空の一覧ではなく前回の内容を見せられる。
/// 別アカウントの一覧を出さないよう、保存したときのユーザー ID が一致する場合だけ使う。
/// </summary>
public static class AvatarListCache
{
    public const string Own = "own";
    public const string Favorites = "favorites";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string PathOf(string kind)
        => AppPaths.In(Path.Combine("cache", $"list-{(kind == Favorites ? Favorites : Own)}.json"));

    public static CachedAvatarList? Load(string kind, string userId)
    {
        var cached = JsonFile.Load<CachedAvatarList>(PathOf(kind), JsonOptions);
        if (cached is null || cached.UserId != userId) return null;
        cached.Avatars = cached.Avatars.Where(a => VRChatApi.IsValidAvatarId(a.Id)).ToList();
        return cached.Avatars.Count > 0 ? cached : null;
    }

    public static void Save(string kind, string userId, IEnumerable<Avatar> avatars)
        => JsonFile.Save(PathOf(kind), new CachedAvatarList
        {
            UserId = userId,
            FetchedAt = DateTimeOffset.Now,
            Avatars = avatars.ToList(),
        }, JsonOptions);

    /// <summary>お気に入りを付け外ししたときなど、内容が古くなったことが分かっている場合に消す。</summary>
    public static void Invalidate(string kind)
    {
        try { File.Delete(PathOf(kind)); } catch { }
    }
}
