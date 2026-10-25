using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Windows の DLL (d3d11 / dxgi / shcore / dwmapi など) は System32 からだけ読む。
// 指定しないと .NET は先に exe と同じフォルダーを探すので、exe を置いたフォルダー (ダウンロードフォルダーなど) に
// 同じ名前の DLL を置かれると、それを読み込んでしまう (DLL の差し込み)。
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace VRCAvatarChanger;

/// <summary>
/// System32 に無い DLL (SteamVR の openvr_api.dll) だけは、置き場所を決めて読み込む。
/// 単一 exe で配るときは、exe に同梱したものが展開先 (%TEMP%\.net\...) に取り出されるので、そこを先に探す
/// (exe の横に置かれた同名の DLL より、同梱したものを優先する)。開発中のビルドでは exe の横にある。
/// </summary>
internal static class NativeLibraries
{
    private const string OpenVr = "openvr_api";

    [ModuleInitializer]
    internal static void Init() => NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals(OpenVr, StringComparison.OrdinalIgnoreCase)) return 0; // ほかは既定どおり (System32)
        return FindOpenVr() is { } path && NativeLibrary.TryLoad(path, out var handle) ? handle : 0;
    }

    /// <summary>openvr_api.dll の場所。同梱品の展開先を先に、無ければ exe の横。見つからなければ null。</summary>
    internal static string? FindOpenVr()
    {
        var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var dirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.TrimEndingDirectorySeparator)
            .ToList();
        var ordered = dirs.Where(d => !d.Equals(appDir, StringComparison.OrdinalIgnoreCase)).Append(appDir);
        return ordered.Select(d => Path.Combine(d, OpenVr + ".dll")).FirstOrDefault(File.Exists);
    }
}
