using System.Text;
using System.Windows.Input;

namespace VRCAvatarChanger;

/// <summary>ホットキーで何をするか。</summary>
public enum HotkeyAction
{
    /// <summary>クイック着替えのオーバーレイを開く / 閉じる。</summary>
    Overlay,
    /// <summary>直前に着ていたアバターに戻す。</summary>
    Previous,
    /// <summary>今のアバターと同じグループの次のアバターに切り替える。</summary>
    NextInGroup,
    /// <summary>決めておいたアバターに直接着替える。</summary>
    Avatar,
}

/// <summary>アバターを直接呼び出すキー割り当て。名前は表示用(一覧に無いアバターでも何か分かるように持つ)。</summary>
public sealed class AvatarHotkey
{
    public string Key { get; set; } = "";
    public string AvatarId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// 修飾キー + キーの組。設定には "Shift+D1" / "Ctrl+Alt+F5" のような文字列で保存する。
/// キーボードフックから来る仮想キーコードとも、WPF のキー入力とも突き合わせられるよう、
/// WPF の <see cref="System.Windows.Input.Key"/> を基準にしている。
/// </summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static readonly Hotkey None = new(ModifierKeys.None, Key.None);

    public bool IsSet => Key != Key.None;

    public static Key FromVirtualKey(int virtualKey)
    {
        try { return KeyInterop.KeyFromVirtualKey(virtualKey); }
        catch { return Key.None; }
    }

    /// <summary>修飾キー単体(Shift だけ など)はホットキーにできない。</summary>
    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin or Key.System or Key.None;

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var modifiers = ModifierKeys.None;
        var key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key) || IsModifierKey(key)) return None;
                    break;
            }
        }
        return key == Key.None ? None : new Hotkey(modifiers, key);
    }

    /// <summary>設定ファイルに保存する形式。</summary>
    public override string ToString()
    {
        if (!IsSet) return "";
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        return sb.Append(Key).ToString();
    }

    /// <summary>画面に出す形式("Shift + 1" など)。未設定なら「未設定」。</summary>
    public string Display
    {
        get
        {
            if (!IsSet) return "未設定";
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    /// <summary>キー名の表示。数字キーは "D1" ではなく "1" と出す。</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "テンキー " + (int)(key - Key.NumPad0),
        Key.Oem3 => "半角/全角",
        Key.OemMinus => "-",
        Key.OemPlus => "^",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Space => "Space",
        _ => key.ToString(),
    };
}
