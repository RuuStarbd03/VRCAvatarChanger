using System.IO;

namespace VRCAvatarChanger;

/// <summary>
/// アプリのデータ保存先 (%AppData%\VRCAvatarChanger)。
/// Debug ビルド限定で VRCAC_DATA_DIR により差し替えられ、UI プレビューなどの検証が
/// 実データ(設定・グループ・セッション)に触れないようにできる。
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Compute();

    private static string Compute()
    {
#if DEBUG
        var o = Environment.GetEnvironmentVariable("VRCAC_DATA_DIR");
        if (!string.IsNullOrEmpty(o)) return o;
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCAvatarChanger");
    }

    /// <summary>DataDir 直下のファイル/フォルダのフルパス。</summary>
    public static string In(string name) => Path.Combine(DataDir, name);
}
