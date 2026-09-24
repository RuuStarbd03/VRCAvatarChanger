namespace VRCAvatarChanger;

/// <summary>表示設定など、機密ではない設定。%AppData%\VRCAvatarChanger\settings.json に平文で保存する。</summary>
public sealed class Settings
{
    public const int MinGridColumns = 3;
    public const int MaxGridColumns = 10;
    public const double MinVrAlongCm = -5, MaxVrAlongCm = 20;
    public const double MinVrHeightCm = -5, MaxVrHeightCm = 5;

    public string ViewMode { get; set; } = "list";   // "list" | "grid"
    /// <summary>配色。"system" (Windows のアプリのモードに合わせる) / "light" / "dark"。</summary>
    public string Theme { get; set; } = "system";
    /// <summary>VRChat 連動: Windows 起動時にトレイで待機し、VRChat の起動を検知したらウィンドウを開く。</summary>
    public bool WatchVRChat { get; set; }
    /// <summary>VRChat のプレイ中に使うホットキー全体のオン/オフ(オフにするとキーボードフックを外す)。</summary>
    public bool QuickOverlay { get; set; } = true;
    /// <summary>
    /// ブラウザログインの内蔵ブラウザに VRChat / Discord / Google のログイン状態を残す。
    /// 残しておくと再ログインが数クリックで済む。オフにすると従来どおり毎回消去する。
    /// </summary>
    public bool KeepBrowserLogin { get; set; } = true;
    /// <summary>VR (SteamVR) の手首ボタンとアバター選択パネル。SteamVR と VRChat が動いているときだけ出る。</summary>
    public bool VrOverlay { get; set; } = true;
    /// <summary>手首ボタンを付ける手。"left" / "right"。</summary>
    public string VrHand { get; set; } = "left";
    /// <summary>手首ボタンを腕時計の位置から肘側へずらす量 (cm)。OVR Toolkit などの腕時計と重ならないように。</summary>
    public double VrButtonAlongCm { get; set; } = 8;
    /// <summary>手首ボタンの高さの微調整 (cm、腕から浮かせる向きが +)。</summary>
    public double VrButtonHeightCm { get; set; }
    // VR パネルの表示状態。メイン画面とは別に持ち、VR の中で切り替えたものを次に開いたときも使う
    /// <summary>VR パネルのタブ。"own" / "favorites" / "public"。</summary>
    public string VrTab { get; set; } = AvatarTab.Own;
    /// <summary>VR パネルの並び順。"recent_asc" / "name_asc"。</summary>
    public string VrSortKey { get; set; } = "recent_asc";
    /// <summary>VR パネルでグループを 1 枚にまとめるか。</summary>
    public bool VrGroupView { get; set; } = true;
    /// <summary>クイック着替えのオーバーレイを開くキー。空 = 割り当てなし。</summary>
    public string QuickHotkey { get; set; } = "Shift+D1";
    /// <summary>直前に着ていたアバターに戻すキー。空 = 割り当てなし。</summary>
    public string PreviousHotkey { get; set; } = "";
    /// <summary>今のアバターと同じグループの次のアバターに切り替えるキー。空 = 割り当てなし。</summary>
    public string NextInGroupHotkey { get; set; } = "";
    /// <summary>特定のアバターを直接呼び出すキー割り当て。</summary>
    public List<AvatarHotkey> AvatarHotkeys { get; set; } = [];
    /// <summary>
    /// クイック着替えの並び順。SortKey と同じ形式。
    /// 旧バージョンは向きなしの "recent" で保存していたので、読み込み時に昇順として扱う。
    /// </summary>
    public string QuickSortKey { get; set; } = "recent_asc";
    /// <summary>最近使用したアバター ID (先頭が最新)。「最近使用した順」の並びに使う。</summary>
    public List<string> RecentAvatars { get; set; } = [];
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

    /// <summary>動作の記録 (app.log) に警告・情報も書くか。オフでもエラーは書く。</summary>
    public bool LogEnabled { get; set; }


    private static string PathOf() => AppPaths.In("settings.json");

    public static Settings Load()
    {
        var s = JsonFile.Load<Settings>(PathOf());
        if (s is null) return new Settings();
        s.GridColumns = Math.Clamp(s.GridColumns, MinGridColumns, MaxGridColumns);
        if (s.ViewMode is not ("list" or "grid")) s.ViewMode = "list";
        if (s.Theme is not ("system" or "light" or "dark")) s.Theme = "system";
        if (s.VrHand is not ("left" or "right")) s.VrHand = "left";
        if (s.VrTab is not (AvatarTab.Own or AvatarTab.Favorites or AvatarTab.Public)) s.VrTab = AvatarTab.Own;
        if (s.VrSortKey is not ("recent_asc" or "name_asc")) s.VrSortKey = "recent_asc";
        s.VrButtonAlongCm = Math.Clamp(s.VrButtonAlongCm, MinVrAlongCm, MaxVrAlongCm);
        s.VrButtonHeightCm = Math.Clamp(s.VrButtonHeightCm, MinVrHeightCm, MaxVrHeightCm);
        return s;
    }

    public void Save() => JsonFile.Save(PathOf(), this, JsonFile.Indented);
}
