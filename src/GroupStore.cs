using System.IO;
using System.Text.Json;

namespace VRCAvatarChanger;

public sealed class AvatarGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> AvatarIds { get; set; } = [];
}

/// <summary>
/// 衣装違いなどをまとめるアプリ独自のグループ。1 アバターは 1 グループにだけ所属する。
/// %AppData%\VRCAvatarChanger\groups.json に平文で保存(機密情報は含まない)。
/// </summary>
public sealed class GroupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<AvatarGroup> _groups = [];

    public IReadOnlyList<AvatarGroup> Groups => _groups;

    private static string PathOf() => AppPaths.In("groups.json");

    public static GroupStore Load()
    {
        var store = new GroupStore();
        var list = JsonFile.Load<List<AvatarGroup>>(PathOf(), JsonOptions);
        if (list is not null)
            store._groups.AddRange(list.Where(g => !string.IsNullOrWhiteSpace(g.Name)));
        return store;
    }

    public void Save() => JsonFile.Save(PathOf(), _groups, JsonOptions);

    public AvatarGroup? GroupOf(string avatarId) => _groups.FirstOrDefault(g => g.AvatarIds.Contains(avatarId));

    /// <summary>アバター ID → 所属グループの逆引き辞書。一覧の再構築時に 1 件ずつ GroupOf で走査しないための索引。</summary>
    public Dictionary<string, AvatarGroup> BuildMembershipIndex()
    {
        var map = new Dictionary<string, AvatarGroup>(StringComparer.Ordinal);
        foreach (var g in _groups)
            foreach (var id in g.AvatarIds) map.TryAdd(id, g);
        return map;
    }

    public AvatarGroup? FindByName(string name)
        => _groups.FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public AvatarGroup Create(string name)
    {
        var g = new AvatarGroup { Name = name.Trim() };
        _groups.Add(g);
        Save();
        return g;
    }

    /// <summary>グループに割り当てる。別のグループに入っていたらそこからは外す。</summary>
    public void Assign(string avatarId, AvatarGroup group)
    {
        foreach (var g in _groups) g.AvatarIds.Remove(avatarId);
        group.AvatarIds.Add(avatarId);
        Save();
    }

    public void Unassign(string avatarId)
    {
        foreach (var g in _groups) g.AvatarIds.Remove(avatarId);
        Save();
    }

    public void Rename(AvatarGroup group, string name)
    {
        group.Name = name.Trim();
        Save();
    }

    public void Delete(AvatarGroup group)
    {
        _groups.Remove(group);
        Save();
    }
}
