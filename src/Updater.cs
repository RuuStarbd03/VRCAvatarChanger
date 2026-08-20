using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace VRCAvatarChanger;

public sealed record UpdateInfo(Version Version, string? ZipUrl, string? ShaUrl, string Notes, string HtmlUrl);

/// <summary>
/// GitHub Releases を使った半自動アップデート。
/// 最新リリースの確認 → zip のダウンロード → SHA-256 検証 → 自分自身を差し替えて再起動。
/// </summary>
public static class Updater
{
    // ★ 配布リポジトリ(例: "kotag/VRCAvatarChanger")。空のままなら更新確認は行わない。
    //   リリースには publish.ps1 が作る VRCAvatarChanger-vX.Y.Z-win-x64.zip と SHA256SUMS.txt を添付すること。
    public const string GitHubRepo = "RuuStarbd03/VRCAvatarChanger";

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    // Debug ビルド限定: テスト用にリポジトリと API の向き先を環境変数で差し替えられる(Release には含まれない)
    private static string Repo
    {
        get
        {
#if DEBUG
            var o = Environment.GetEnvironmentVariable("VRCAC_UPDATE_REPO");
            if (!string.IsNullOrEmpty(o)) return o;
#endif
            return GitHubRepo;
        }
    }

    private static string ApiBase
    {
        get
        {
#if DEBUG
            var o = Environment.GetEnvironmentVariable("VRCAC_UPDATE_API");
            if (!string.IsNullOrEmpty(o)) return o;
#endif
            return "https://api.github.com";
        }
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VRCAvatarChanger/" + CurrentVersion.ToString(3) + " (" + VRChatApi.Contact + ")");
        return http;
    }

    /// <summary>新しいバージョンがあれば UpdateInfo、無ければ null。確認できないときも null(静かに諦める)。</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (string.IsNullOrEmpty(Repo)) return null;
        try
        {
            using var http = NewHttp();
            var json = await http.GetStringAsync($"{ApiBase}/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // バージョンはタグ名 (v1.2.3) から取る。タグが別名のリリースでも動くよう、リリース名でも試す
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var relName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) &&
                !Version.TryParse(relName.TrimStart('v', 'V'), out version)) return null;
            if (version <= CurrentVersion) return null;

            string? zipUrl = null, shaUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase)) zipUrl = url;
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new UpdateInfo(version, zipUrl, shaUrl, notes, htmlUrl);
        }
        catch { return null; }
    }

    /// <summary>
    /// zip をダウンロードして検証し、実行中の exe を差し替えて新しいバージョンを起動する。
    /// 成功したら戻らない(アプリを終了する)。失敗時は例外。
    /// </summary>
    public static async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        if (info.ZipUrl is null) throw new InvalidOperationException("このリリースには自動更新用のファイルが添付されていません。");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所が分かりません。");
        var workDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCAvatarChanger", "update");
        Directory.CreateDirectory(workDir);
        var zipPath = Path.Combine(workDir, "update.zip");

        using (var http = NewHttp())
        {
            var bytes = await http.GetByteArrayAsync(info.ZipUrl);
            await File.WriteAllBytesAsync(zipPath, bytes);

            // SHA256SUMS.txt があれば照合する(HTTPS に加えた整合性チェック)
            if (info.ShaUrl is not null)
            {
                var sums = await http.GetStringAsync(info.ShaUrl);
                var fileName = Path.GetFileName(new Uri(info.ZipUrl).LocalPath);
                var expected = sums.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Split(' ', '\t')[0].Trim().ToLowerInvariant())
                    .FirstOrDefault();
                if (expected is not null)
                {
                    var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (actual != expected)
                        throw new InvalidOperationException("ダウンロードしたファイルの検証に失敗しました。更新を中止します。");
                }
            }
        }

        // zip から新しい exe を取り出す
        var newExe = Path.Combine(workDir, "VRCAvatarChanger.exe.new");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("VRCAvatarChanger.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("更新ファイルの中に VRCAvatarChanger.exe が見つかりません。");
            entry.ExtractToFile(newExe, overwrite: true);
        }
        File.Delete(zipPath);

        // 実行中の exe はリネームできるので、.old に退避してから新しい exe を置く
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(old, exe); // 失敗したら元に戻す
            throw;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>前回の更新で残った .old を消す。起動時に呼ぶ。</summary>
    public static void CleanupOldVersion()
    {
        try
        {
            var old = (Environment.ProcessPath ?? "") + ".old";
            if (old.Length > 4 && File.Exists(old)) File.Delete(old);
        }
        catch { /* まだ掴まれていたら次回 */ }
    }
}
