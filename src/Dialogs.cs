using System.Windows;

namespace VRCAvatarChanger;

/// <summary>利用者に確かめる / 伝えるための小さなダイアログ。タイトルとアイコンをアプリ内で揃える。</summary>
internal static class Dialogs
{
    private const string Title = "VRCAvatarChanger";

    /// <summary>「OK / キャンセル」で確かめる。OK なら true。</summary>
    public static bool Confirm(Window owner, string text)
        => MessageBox.Show(owner, text, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>「はい / いいえ」で確かめる。はいなら true。</summary>
    public static bool Ask(Window owner, string text)
        => MessageBox.Show(owner, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>「はい / いいえ / キャンセル」で確かめる。</summary>
    public static MessageBoxResult AskOrCancel(Window owner, string text)
        => MessageBox.Show(owner, text, Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

    /// <summary>エラーを伝える。owner が無いとき (起動前など) は null でよい。</summary>
    public static void Error(Window? owner, string text)
    {
        if (owner is not null) MessageBox.Show(owner, text, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(text, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
