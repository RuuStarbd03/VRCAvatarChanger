using System.IO;

namespace VRCAvatarChanger;

/// <summary>
/// 保存中にクラッシュ・電源断が起きても既存ファイルが壊れないよう、
/// 一時ファイルに書き切ってから置き換える。
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
        => Write(path, tmp => File.WriteAllText(tmp, contents));

    public static void WriteAllBytes(string path, byte[] bytes)
        => Write(path, tmp => File.WriteAllBytes(tmp, bytes));

    private static void Write(string path, Action<string> writeTo)
    {
        var tmp = path + ".tmp";
        writeTo(tmp);
        File.Move(tmp, path, overwrite: true);
    }
}
