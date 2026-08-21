using System.IO;
using System.Text.Json;

namespace VRCAvatarChanger;

/// <summary>
/// アバターに付けるタグ(絞り込み用)。タブに関係なくアバター ID 単位で持つ。
/// %AppData%\VRCAvatarChanger\tags.json に平文で保存(機密情報は含まない)。
/// </summary>
public sealed class TagStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, List<string>> _tags = new(StringComparer.Ordinal);

    private static string PathOf() => AppPaths.In("tags.json");

    public static TagStore Load()
    {
        var store = new TagStore();
        var map = JsonFile.Load<Dictionary<string, List<string>>>(PathOf(), JsonOptions);
        if (map is not null)
            foreach (var (id, tags) in map.Where(kv => VRChatApi.IsValidAvatarId(kv.Key)))
                store._tags[id] = Normalize(tags);
        return store;
    }

    public void Save() => JsonFile.Save(PathOf(), _tags.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value), JsonOptions);

    private static List<string> Normalize(IEnumerable<string> tags)
        => tags.Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>使われているすべてのタグ(名前順)。</summary>
    public List<string> AllTags()
        => _tags.Values.SelectMany(t => t)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public IReadOnlyList<string> TagsOf(string avatarId)
        => _tags.TryGetValue(avatarId, out var tags) ? tags : [];

    public void SetTags(string avatarId, IEnumerable<string> tags)
    {
        var list = Normalize(tags);
        if (list.Count == 0) _tags.Remove(avatarId);
        else _tags[avatarId] = list;
        Save();
    }
}
