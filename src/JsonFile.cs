using System.IO;
using System.Text.Json;

namespace VRCAvatarChanger;

/// <summary>
/// 設定・各ストア共通の JSON ファイル入出力。
/// 読み込みはファイルが無い場合に null (呼び出し側が既定値から始める)。壊れていた場合も null だが、
/// 黙って既定値に戻すとグループやタグが「消えた」ように見えるので、壊れたファイルは別名で残し、
/// 記録して画面にも伝える (<see cref="Failed"/>)。保存はアトミックで、失敗しても例外にはしないが同様に伝える。
/// </summary>
public static class JsonFile
{
    /// <summary>読み書きに失敗したときに、利用者向けの一文を渡す。UI スレッドとは限らない。</summary>
    public static event Action<string>? Failed;

    // 起動直後 (画面ができる前) の失敗は、誰も聞いていないので溜めておき、購読が始まったら流す
    private static readonly List<string> Pending = [];

    private static void Notify(string message)
    {
        lock (Pending)
        {
            if (Failed is null) { Pending.Add(message); return; }
        }
        Failed.Invoke(message);
    }

    /// <summary>購読を始めたあとに呼ぶ。画面ができる前に起きた失敗をまとめて渡す。</summary>
    public static void FlushPending()
    {
        List<string> items;
        lock (Pending) { items = [.. Pending]; Pending.Clear(); }
        foreach (var m in items) Failed?.Invoke(m);
    }

    public static T? Load<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (Exception ex)
        {
            // 壊れたファイルを上書きしてしまわないよう退避する (手で直せば戻せる)
            var backup = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Move(path, backup, overwrite: true); } catch (Exception mv) { Log.Error($"壊れたファイルを退避できませんでした: {path}", mv); backup = "(退避できず)"; }
            Log.Error($"読み込めませんでした (既定値から始めます): {path} → 退避先 {backup}", ex);
            Notify($"{Path.GetFileName(path)} を読めなかったため、既定の状態から始めました (元のファイルは {Path.GetFileName(backup)} として残しています)");
            return null;
        }
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, options));
        }
        catch (Exception ex)
        {
            Log.Error($"保存できませんでした: {path}", ex);
            Notify($"{Path.GetFileName(path)} を保存できませんでした ({ex.Message})");
        }
    }
}
