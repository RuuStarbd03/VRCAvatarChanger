namespace VRCAvatarChanger;

/// <summary>表示設定など、機密ではない設定。%AppData%\VRCAvatarChanger\settings.json に平文で保存する。</summary>
public sealed class Settings
{
    public const int MinGridColumns = 3;
    public const int MaxGridColumns = 10;

    public string ViewMode { get; set; } = "list";   // "list" | "grid"
    /// <summary>配色。"system" (Windows のアプリのモードに合わせる) / "light" / "dark"。</summary>
    public string Theme { get; set; } = "system";
    /// <summary>アクセント色。"system" (Windows のアクセントカラーに合わせる) か、AccentPalette.Presets のキー。</summary>
    public string Accent { get; set; } = AccentPalette.System;
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
    /// <summary>VR のボタンを付ける手。"left" / "right"。</summary>
    public string VrHand { get; set; } = "left";
    /// <summary>
    /// VR のボタンをつかんで動かした位置 (付けた手のコントローラーから見た位置と向き、3x4 の 12 個)。
    /// null なら手の甲 (既定の位置)。
    /// </summary>
    public float[]? VrButtonPose { get; set; }
    // VR パネルの表示状態。メイン画面とは別に持ち、VR の中で切り替えたものを次に開いたときも使う
    /// <summary>VR パネルのタブ。"own" / "favorites" / "public"。</summary>
    public string VrTab { get; set; } = AvatarTab.Own;
    /// <summary>VR パネルの並び順。SortKey と同じ形式 (種類は VrPanel.SortKinds の 6 種類)。</summary>
    public string VrSortKey { get; set; } = "recent_asc";
    /// <summary>VR パネルでグループを 1 枚にまとめるか。</summary>
    public bool VrGroupView { get; set; } = true;
    /// <summary>VR パネルの列数 (3〜10)。段の数は列数から決まる。</summary>
    public int VrColumns { get; set; } = 3;
    /// <summary>クイック着替えのオーバーレイを開くキー。空 = 割り当てなし。</summary>
    public string QuickHotkey { get; set; } = "Ctrl+Shift+D1";
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
        if (!AccentPalette.IsValid(s.Accent)) s.Accent = AccentPalette.System;
        if (s.VrHand is not ("left" or "right")) s.VrHand = "left";
        if (s.VrTab is not (AvatarTab.Own or AvatarTab.Favorites or AvatarTab.Public)) s.VrTab = AvatarTab.Own;
        if (!VrPanel.IsValidSortKey(s.VrSortKey)) s.VrSortKey = "recent_asc";
        if (!VrPanel.IsValidSortKey(s.SortKey)) s.SortKey = "created_desc";       // 並びの種類はメイン画面・クイックも同じ 6 種類
        if (!VrPanel.IsValidSortKey(s.QuickSortKey)) s.QuickSortKey = "recent_asc";
        // 手で書き換えられた / 壊れたファイルの null で、起動後に落ちないようにする
        s.RecentAvatars = s.RecentAvatars?.Where(VRChatApi.IsValidAvatarId).ToList() ?? [];
        s.AvatarHotkeys = s.AvatarHotkeys?.Where(h => h is not null).ToList() ?? [];
        s.StripeExcluded = s.StripeExcluded?.Where(id => id is not null).ToList() ?? [];
        s.VrColumns = Math.Clamp(s.VrColumns, MinGridColumns, MaxGridColumns);
        if (VRCAvatarChanger.VrOverlay.PoseFromArray(s.VrButtonPose) is null) s.VrButtonPose = null; // 壊れた値・遠すぎる位置は手の甲に戻す
        return s;
    }

    public void Save() => JsonFile.Save(PathOf(), this, JsonFile.Indented);
}
