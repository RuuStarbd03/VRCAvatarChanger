using System.IO;
using System.Text.Json;

namespace VRCAvatarChanger;

/// <summary>
/// 設定・各ストア共通の JSON ファイル入出力。
/// 読み込みはファイルが無い・壊れている場合に null(呼び出し側が既定値から始める)、
/// 保存はアトミックで、失敗しても例外にしない(保存できなくても動作には影響しない)。
/// </summary>
public static class JsonFile
{
    public static T? Load<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch { /* 壊れていたら既定値から */ }
        return null;
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, options));
        }
        catch { /* 保存失敗は致命的ではない */ }
    }
}
