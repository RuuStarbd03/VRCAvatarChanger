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
///
/// タブの行き来やトレイからの復帰のたびに呼ばれるので、ファイルを読むのは種類ごとに最初の 1 回だけにして、
/// 以降はメモリに持っている内容を返す (1000 体で 1 回 8ms 前後かかり、その間 UI が止まるため)。
/// 書き出しも同じ理由で UI スレッドの外で行う。
/// </summary>
public static class AvatarListCache
{
    public const string Own = AvatarTab.Own;
    public const string Favorites = AvatarTab.Favorites;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>種類ごとの、メモリ上の最新内容。まだファイルを読んでいなければ入っていない (消したものは null)。</summary>
    private static readonly Dictionary<string, CachedAvatarList?> Memory = [];

    /// <summary>
    /// 種類ごとの世代。保存・消去のたびに進める。裏で書き出す前にこれを見て、
    /// 後から保存・消去されていたら古い内容で上書きしない (書き出しの順番が前後しても最新が残る)。
    /// </summary>
    private static readonly Dictionary<string, int> Generation = [];

    /// <summary>Memory と Generation を守る (すぐ終わる処理だけ)。</summary>
    private static readonly object Gate = new();

    /// <summary>ファイルの書き出し・削除を 1 つずつにする。UI スレッドの読み込みを書き出しで待たせないよう Gate とは分ける。</summary>
    private static readonly object FileGate = new();

    private static bool IsCurrent(string kind, int gen)
    {
        lock (Gate) return Generation.GetValueOrDefault(kind) == gen;
    }

    private static string Normalize(string kind) => kind == Favorites ? Favorites : Own;

    private static string PathOf(string kind) => AppPaths.In(Path.Combine("cache", $"list-{kind}.json"));

    public static CachedAvatarList? Load(string kind, string userId)
    {
        kind = Normalize(kind);
        CachedAvatarList? cached;
        lock (Gate)
        {
            if (!Memory.TryGetValue(kind, out cached))
            {
                cached = JsonFile.Load<CachedAvatarList>(PathOf(kind), JsonOptions);
                if (cached is not null) cached.Avatars = cached.Avatars.Where(a => VRChatApi.IsValidAvatarId(a.Id)).ToList();
                Memory[kind] = cached;
            }
        }
        if (cached is null || cached.UserId != userId) return null;
        return cached.Avatars.Count > 0 ? cached : null;
    }

    public static void Save(string kind, string userId, IEnumerable<Avatar> avatars)
    {
        kind = Normalize(kind);
        var list = new CachedAvatarList
        {
            UserId = userId,
            FetchedAt = DateTimeOffset.Now,
            Avatars = avatars.Where(a => VRChatApi.IsValidAvatarId(a.Id)).ToList(),
        };
        int gen;
        lock (Gate)
        {
            Memory[kind] = list;
            gen = Generation[kind] = Generation.GetValueOrDefault(kind) + 1;
        }
        Task.Run(() =>
        {
            // 消去 (Invalidate) や後の保存と入れ違わないよう、世代の確認と書き出しを FileGate の中でまとめて行う
            lock (FileGate)
            {
                if (!IsCurrent(kind, gen)) return; // もっと新しい保存・消去があった
                JsonFile.Save(PathOf(kind), list, JsonOptions);
            }
        }).Forget();
    }

#if DEBUG
    /// <summary>検証用: メモリ上の内容を捨て、次の Load でファイルから読ませる (起動直後と同じ状態)。</summary>
    internal static void DropMemory()
    {
        lock (Gate) Memory.Clear();
    }
#endif

    /// <summary>お気に入りを付け外ししたときなど、内容が古くなったことが分かっている場合に消す。</summary>
    public static void Invalidate(string kind)
    {
        kind = Normalize(kind);
        lock (Gate)
        {
            Memory[kind] = null;
            Generation[kind] = Generation.GetValueOrDefault(kind) + 1;
        }
        // 書き出し中ならそれが終わるのを待ってから消す (消したあとに古い内容が書かれて残らないように)
        lock (FileGate)
        {
            try { File.Delete(PathOf(kind)); } catch { }
        }
    }
}
