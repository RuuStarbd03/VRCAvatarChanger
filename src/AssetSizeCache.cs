using System.IO;
using System.Text.Json;

namespace VRCAvatarChanger;

/// <summary>
/// アバターのダウンロードサイズ (%AppData%\VRCAvatarChanger\cache\asset-sizes.json)。
///
/// サイズはアバターの応答に入っておらず、ファイルの情報を 1 件 1 リクエストで引くしかない。
/// 同じファイルの同じバージョンならサイズは変わらないので、一度引いたら永続的に使い回す。
/// (消しても次に見たときに引き直すだけ)
/// </summary>
public static class AssetSizeCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static Dictionary<string, long>? _map;
    private static bool _dirty;

    private static string PathOf() => AppPaths.In(Path.Combine("cache", "asset-sizes.json"));

    private static string KeyOf(string fileId, int version) => $"{fileId}/{version}";

    private static Dictionary<string, long> Map
        => _map ??= JsonFile.Load<Dictionary<string, long>>(PathOf(), JsonOptions) ?? [];

    public static bool TryGet(string fileId, int version, out long bytes)
        => Map.TryGetValue(KeyOf(fileId, version), out bytes);

    public static void Set(string fileId, int version, long bytes)
    {
        Map[KeyOf(fileId, version)] = bytes;
        _dirty = true;
    }

    /// <summary>まとめて書き出す (1 件ごとに書くと取得のたびにファイルを触ることになるため)。</summary>
    public static void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        JsonFile.Save(PathOf(), Map, JsonOptions);
    }

    /// <summary>「31.4 MB」「820 KB」のような表示。</summary>
    public static string Format(long bytes)
        => bytes >= 1024L * 1024
            ? $"{bytes / 1024.0 / 1024.0:F1} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";
}
