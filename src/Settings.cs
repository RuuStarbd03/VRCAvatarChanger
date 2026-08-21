using System.IO;
using System.Text.Json;

namespace VRCAvatarChanger;

/// <summary>表示設定など、機密ではない設定。%AppData%\VRCAvatarChanger\settings.json に平文で保存する。</summary>
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

    // ウィンドウの位置とサイズ(前回終了時)
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string PathOf() => AppPaths.In("settings.json");

    public static Settings Load()
    {
        var s = JsonFile.Load<Settings>(PathOf());
        if (s is null) return new Settings();
        s.GridColumns = Math.Clamp(s.GridColumns, MinGridColumns, MaxGridColumns);
        if (s.ViewMode is not ("list" or "grid")) s.ViewMode = "list";
        return s;
    }

    public void Save() => JsonFile.Save(PathOf(), this, JsonOptions);
}
