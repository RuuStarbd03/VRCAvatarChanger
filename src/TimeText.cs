namespace VRCAvatarChanger;

/// <summary>日時の表示。画面に出す形はここで揃える (ローカル時刻)。</summary>
internal static class TimeText
{
    /// <summary>「2026/09/02」。無ければ null (呼び出し側が代わりの文字を選ぶ)。</summary>
    public static string? Date(DateTimeOffset? d) => d?.ToLocalTime().ToString("yyyy/MM/dd");

    /// <summary>「2026/09/02 09:31」。無ければ null。</summary>
    public static string? DateTime(DateTimeOffset? d) => d?.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}
