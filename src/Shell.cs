using System.Diagnostics;

namespace VRCAvatarChanger;

/// <summary>ほかのプログラムとのやりとりの小物。</summary>
internal static class Shell
{
    /// <summary>既定のアプリで開く (URL はブラウザ、フォルダはエクスプローラー、ファイルは関連付け)。失敗は例外のまま呼び出し側へ。</summary>
    public static void Open(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>
    /// その名前のプロセスが動いているか (VRChat の起動の見張りに使う)。
    /// 全プロセスを見るので数 ms かかる。UI スレッドの外から呼ぶこと。
    /// </summary>
    public static bool IsRunning(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>
    /// それぞれの名前のプロセスが動いているか (VRChat と SteamVR をまとめて見張るのに使う)。
    /// GetProcessesByName は 1 回ごとに全プロセスの一覧を取り直す (1 回 8ms ほど) ので、
    /// 2 つの名前なら一覧を 1 回だけ取って両方と比べる。UI スレッドの外から呼ぶこと。
    /// </summary>
    public static bool[] AreRunning(params string[] processNames)
    {
        var found = new bool[processNames.Length];
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                for (var i = 0; i < processNames.Length; i++)
                    if (!found[i] && string.Equals(p.ProcessName, processNames[i], StringComparison.OrdinalIgnoreCase)) found[i] = true;
            }
        }
        return found;
    }
}
