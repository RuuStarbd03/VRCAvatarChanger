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
    private readonly List<AvatarGroup> _groups = [];

    public IReadOnlyList<AvatarGroup> Groups => _groups;

    private static string PathOf() => AppPaths.In("groups.json");

    public static GroupStore Load()
    {
        var store = new GroupStore();
        var list = JsonFile.Load<List<AvatarGroup>>(PathOf(), JsonFile.Indented);
        if (list is not null)
        {
            // 手で書き換えられた / 壊れたファイルの null で、起動できなくならないようにする
            var valid = list.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name)).ToList();
            foreach (var g in valid)
            {
                g.Id ??= Guid.NewGuid().ToString("N");
                g.AvatarIds = g.AvatarIds?.Where(VRChatApi.IsValidAvatarId).ToList() ?? [];
            }
            store._groups.AddRange(valid);
        }
        return store;
    }

    public void Save() => JsonFile.Save(PathOf(), _groups, JsonFile.Indented);

    public AvatarGroup? GroupOf(string avatarId) => _groups.FirstOrDefault(g => g.AvatarIds.Contains(avatarId));

    /// <summary>アバター ID → 所属グループの逆引き辞書。一覧の再構築時に 1 件ずつ GroupOf で走査しないための索引。</summary>
    public Dictionary<string, AvatarGroup> BuildMembershipIndex()
    {
        var map = new Dictionary<string, AvatarGroup>(StringComparer.Ordinal);
        foreach (var g in _groups)
            foreach (var id in g.AvatarIds) map.TryAdd(id, g);
        return map;
    }

    /// <summary>今のグループ構成の写し (取り消し用)。後から元のグループを変えても写しは変わらない。</summary>
    public List<AvatarGroup> Snapshot() => _groups.Select(Clone).ToList();

    /// <summary>写しの状態に戻して保存する。</summary>
    public void Restore(IEnumerable<AvatarGroup> snapshot)
    {
        _groups.Clear();
        _groups.AddRange(snapshot.Select(Clone));
        Save();
    }

    private static AvatarGroup Clone(AvatarGroup g) => new() { Id = g.Id, Name = g.Name, AvatarIds = [.. g.AvatarIds] };

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
    public void Assign(string avatarId, AvatarGroup group) => Assign([avatarId], group);

    /// <summary>まとめて割り当てる。グループ同士の統合などで、1 体ごとにファイルを書き直さないため。</summary>
    public void Assign(IEnumerable<string> avatarIds, AvatarGroup group)
    {
        foreach (var avatarId in avatarIds)
        {
            foreach (var g in _groups) g.AvatarIds.Remove(avatarId);
            group.AvatarIds.Add(avatarId);
        }
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
