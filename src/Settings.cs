using System.IO;
using System.Text.Json;

namespace VRCAvatarChanger;

/// <summary>è¡¨ç¤ºè¨­å®ãªã©ãæ©å¯ã§ã¯ãªãè¨­å®ã%AppData%\VRCAvatarChanger\settings.json ã«å¹³æã§ä¿å­ããã</summary>
public sealed class Settings
{
    public const int MinGridColumns = 3;
    public const int MaxGridColumns = 10;

    public string ViewMode { get; set; } = "list";   // "list" | "grid"
    public int GridColumns { get; set; } = 5;
    public string SortKey { get; set; } = "created_desc"; // created/updated/name + _asc/_desc
    public bool GroupView { get; set; } = true; // 既定はグループ化して表示

    // 隠し機能 (Ctrl+Shift+C): 一覧を 10 体ごとに色分けする
    public bool StripeColors { get; set; }
    public List<string> StripeExcluded { get; set; } = []; // カウントから除外する ID (アバター ID / group:グループ ID)

    // ã¦ã£ã³ãã¦ã®ä½ç½®ã¨ãµã¤ãº(ååçµäºæ)
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static string PathOf() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCAvatarChanger", "settings.json");

    public static Settings Load()
    {
        try
        {
            var p = PathOf();
            if (File.Exists(p))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(p));
                if (s is not null)
                {
                    s.GridColumns = Math.Clamp(s.GridColumns, MinGridColumns, MaxGridColumns);
                    if (s.ViewMode is not ("list" or "grid")) s.ViewMode = "list";
                    return s;
                }
            }
        }
        catch { /* å£ãã¦ãããæ¢å®å¤ */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var p = PathOf();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ä¿å­ã§ããªãã¦ãåä½ã«ã¯å½±é¿ããªã */ }
    }
}
