using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VRCAvatarChanger;

/// <summary>
/// アクセント色 (ボタン・選択中の印・チェックマーク・グループ化の光など)。
/// 既定は Windows のアクセントカラー (設定 → 個人用設定 → 色) に合わせ、設定で好きな色に固定もできる。
///
/// 1 色から、テーマ (ライト / ダーク) に合わせて使う色一式を作る:
///   ・アクセント本体は、ライトでは白い文字が、ダークでは暗い背景の上で読める明るさに寄せる
///   ・ホバー / 押した時の色、選択中の行の背景色 (背景にアクセントを薄く混ぜたもの)
///   ・アクセントの上に載せる文字色 (白か黒の、読みやすい方。明るいアクセントなら黒)
/// Windows に合わせる場合は、Windows が持つ濃淡の段から Windows 自身と同じ段を使う
/// (ライトは濃いめの Dark1、ダークは明るめの Light2)。
/// </summary>
public static class AccentPalette
{
    /// <summary>Windows に合わせる。</summary>
    public const string System = "system";

    /// <summary>選べる色。キーは設定に保存する名前。</summary>
    public static readonly (string Key, string Label, Color Color)[] Presets =
    [
        ("blue", "青", Color.FromRgb(0x3E, 0x8F, 0xD0)),
        ("purple", "紫", Color.FromRgb(0x8E, 0x6B, 0xD8)),
        ("pink", "桃", Color.FromRgb(0xD8, 0x5F, 0x9E)),
        ("red", "赤", Color.FromRgb(0xD9, 0x55, 0x55)),
        ("orange", "橙", Color.FromRgb(0xDE, 0x7E, 0x34)),
        ("green", "緑", Color.FromRgb(0x3A, 0xA0, 0x6A)),
    ];

    /// <summary>Windows のアクセントカラーが取れないときの色 (以前の既定の青)。</summary>
    private static readonly Color Fallback = Presets[0].Color;

    // 各テーマの背景 (Themes/*.xaml の WindowBrush と同じ)。選択色を混ぜるときと、読みやすさを測るときに使う
    private static readonly Color LightBack = Colors.White;
    private static readonly Color DarkBack = Color.FromRgb(0x14, 0x14, 0x16);

    public static bool IsValid(string? key) => key == System || Presets.Any(p => p.Key == key);

    /// <summary>テーマで使うアクセント本体の色。</summary>
    public static Color AccentFor(string key, bool dark)
    {
        if (key == System && SystemAccentShade(dark) is { } shade) return Readable(shade, dark);
        var baseColor = Presets.FirstOrDefault(p => p.Key == key).Color;
        if (baseColor == default) baseColor = key == System ? SystemAccentBase() ?? Fallback : Fallback;
        return Readable(baseColor, dark);
    }

    /// <summary>テーマの辞書に、アクセント一式を書き込む (Themes/*.xaml の青を上書きする)。</summary>
    public static void ApplyTo(ResourceDictionary dict, string key, bool dark)
    {
        var accent = AccentFor(key, dark);
        dict["AccentBrush"] = Frozen(accent);
        dict["AccentHoverBrush"] = Frozen(dark ? Mix(accent, Colors.White, 0.12) : Mix(accent, Colors.Black, 0.10));
        dict["AccentPressedBrush"] = Frozen(Mix(accent, Colors.Black, dark ? 0.12 : 0.20));
        dict["OnAccentBrush"] = Frozen(OnAccentFor(accent, dark));
        dict["SelectionBrush"] = Frozen(dark ? Mix(DarkBack, accent, 0.35) : Mix(LightBack, accent, 0.24));
    }

    /// <summary>アクセントの上に載せる文字色。白と黒のうち、アクセントとの差 (コントラスト) が大きい方。</summary>
    public static Color OnAccentFor(Color accent, bool dark)
    {
        var black = Color.FromRgb(0x11, 0x11, 0x13);
        return Contrast(Colors.White, accent) >= Contrast(black, accent)
            ? (dark ? Color.FromRgb(0xF6, 0xFA, 0xFE) : Colors.White)
            : black;
    }

    // ---------------- Windows のアクセントカラー ----------------

    /// <summary>
    /// Windows のアクセントの濃淡 (AccentPalette: Light3, Light2, Light1, 本体, Dark1, Dark2, Dark3 の順で RGBA) から、
    /// テーマに合う段を取る。読めなければ null。
    /// </summary>
    private static Color? SystemAccentShade(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is not byte[] { Length: >= 28 } p) return null;
            var i = (dark ? 1 : 4) * 4; // ダーク: Light2 / ライト: Dark1
            return Color.FromRgb(p[i], p[i + 1], p[i + 2]);
        }
        catch (Exception ex) { Log.Debug("Windows のアクセントカラーを読めませんでした", ex); return null; }
    }

    /// <summary>濃淡が取れないとき用。DWM のアクセントカラー (ABGR の DWORD)。</summary>
    private static Color? SystemAccentBase()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is not int abgr) return null;
            return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch (Exception ex) { Log.Debug("Windows のアクセントカラーを読めませんでした", ex); return null; }
    }

    // ---------------- 色の計算 ----------------

    /// <summary>
    /// 読みやすい明るさに寄せる。ライト: 白い文字を載せて読める (コントラスト 4.5 以上) まで暗く。
    /// ダーク: 暗い背景の上で読める (4.5 以上) まで明るく。もともと足りていればそのまま。
    /// </summary>
    internal static Color Readable(Color c, bool dark)
    {
        for (var i = 0; i < 20; i++)
        {
            if (dark ? Contrast(c, DarkBack) >= 4.5 : Contrast(Colors.White, c) >= 4.5) break;
            c = dark ? Mix(c, Colors.White, 0.08) : Mix(c, Colors.Black, 0.08);
        }
        return c;
    }

    /// <summary>a に b を t の割合で混ぜる。</summary>
    internal static Color Mix(Color a, Color b, double t)
        => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>2 色のコントラスト比 (WCAG)。1〜21。</summary>
    internal static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
