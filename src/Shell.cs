using System.Diagnostics;

namespace VRCAvatarChanger;

/// <summary>既定のアプリで開く (URL はブラウザ、フォルダはエクスプローラー、ファイルは関連付け)。失敗は例外のまま呼び出し側へ。</summary>
internal static class Shell
{
    public static void Open(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
