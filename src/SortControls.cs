using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VRCAvatarChanger;

/// <summary>
/// 並び順の UI (種別のコンボ + 昇順 / 降順のトグル) の共通処理。
/// メイン画面とクイック着替えの両方に同じ組があり、保存キー ("created_desc" など) との変換も同じ。
/// </summary>
internal static class SortControls
{
    /// <summary>保存・並べ替えに使うキー。種別と向きを "kind_desc" / "kind_asc" に組み直す。</summary>
    public static string Compose(ComboBox kindBox, ToggleButton descToggle, string defaultKind)
        => ((kindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? defaultKind)
           + (descToggle.IsChecked == true ? "_desc" : "_asc");

    /// <summary>保存されたキーを、種別のコンボと向きのトグルに振り分ける。知らない種別なら先頭。</summary>
    public static void Init(ComboBox kindBox, ToggleButton descToggle, string savedKey)
    {
        var desc = savedKey.EndsWith("_desc", StringComparison.Ordinal);
        var kind = savedKey.Replace("_desc", "").Replace("_asc", "");
        kindBox.SelectedItem = kindBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == kind)
                               ?? kindBox.Items[0];
        descToggle.IsChecked = desc;
    }

    /// <summary>向きの見た目 (矢印とツールチップ) を今の状態に合わせる。向きの意味は種別で変わるので、言い方もそれに合わせる。</summary>
    public static void UpdateDirection(ComboBox kindBox, ToggleButton descToggle, TextBlock arrow)
    {
        var desc = descToggle.IsChecked == true;
        arrow.Text = desc ? "↓" : "↑";
        descToggle.ToolTip = (string?)(kindBox.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "name" => desc ? "名前の降順 (Z → A)" : "名前の昇順 (A → Z)",
            "author" => desc ? "作者名の降順 (Z → A)" : "作者名の昇順 (A → Z)",
            "recent" => desc ? "使っていないものから" : "最近使ったものから",
            "performance" => desc ? "重い順" : "軽い順",
            _ => desc ? "新しい順" : "古い順",
        };
    }
}
