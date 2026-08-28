using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VRCAvatarChanger;

/// <summary>
/// サムネイル画像のディスクキャッシュ (%AppData%\VRCAvatarChanger\cache\thumbs)。
/// これが無いと起動のたびに全サムネを取り直すことになり、起動が遅いうえレート制限にも当たりやすい。
///
/// ファイル名は URL の SHA-256 (16 進)。VRChat のサムネ URL には画像のバージョンが入るので、
/// アバターの画像が差し替わると別のキーになり、古いものは容量整理で自然に落ちる。
/// 取得できない・書けない場合は「キャッシュが無い」として扱い、動作には影響させない。
/// </summary>
public static class ImageDiskCache
{
    /// <summary>この容量を超えたら、最後に使われたのが古いものから消す。320px デコード前の元画像で 1 枚 20〜80KB 程度。</summary>
    private const long MaxBytes = 300L * 1024 * 1024;

    /// <summary>この量を書いたら整理する。開きっぱなしでも上限を超えたままにならないように。</summary>
    private const long TrimEveryBytes = 50L * 1024 * 1024;

    private static long _writtenSinceTrim;

    private static string Dir => AppPaths.In(Path.Combine("cache", "thumbs"));

    private static string PathOf(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Path.Combine(Dir, Convert.ToHexString(hash) + ".img");
    }

    /// <summary>キャッシュにあれば返す。無ければ null。</summary>
    public static async Task<byte[]?> TryReadAsync(string url, CancellationToken ct)
    {
        try
        {
            var path = PathOf(url);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            // 容量整理の順序付けに使う「最後に使った日時」。毎回書くと無駄なので 1 日に 1 回だけ
            if (info.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-1))
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
            return bytes;
        }
        catch { return null; }
    }

    public static async Task WriteAsync(string url, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = PathOf(url);
            // 書き途中のファイルを読ませないよう、別名に書いてから置き換える
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            // 起動時だけの整理だと、開きっぱなしのときに上限を超えたままになる
            if (Interlocked.Add(ref _writtenSinceTrim, bytes.Length) > TrimEveryBytes)
            {
                Interlocked.Exchange(ref _writtenSinceTrim, 0);
                _ = Task.Run(Trim);
            }
        }
        catch { /* 保存できなくても次回また取りに行くだけ */ }
    }

    public static void Delete(string url)
    {
        try { File.Delete(PathOf(url)); } catch { }
    }

    /// <summary>
    /// 上限を超えた分を「最後に使ったのが古い順」に消す。起動時に 1 回だけ裏で呼ぶ。
    /// 消しすぎて毎回削除が走らないよう、上限の 8 割まで落とす。
    /// </summary>
    public static void Trim()
    {
        try
        {
            var dir = new DirectoryInfo(Dir);
            if (!dir.Exists) return;
            // 中断された書き込みの残骸を片付ける (容量に関係なく毎回)
            foreach (var leftover in dir.GetFiles("*.tmp")) try { leftover.Delete(); } catch { }

            var files = dir.GetFiles("*.img");
            var total = files.Sum(f => f.Length);
            if (total <= MaxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= MaxBytes * 8 / 10) break;
                var size = f.Length; // Delete すると FileInfo の情報が無効になるので先に控える
                try { f.Delete(); total -= size; } catch { }
            }
        }
        catch { }
    }
}
