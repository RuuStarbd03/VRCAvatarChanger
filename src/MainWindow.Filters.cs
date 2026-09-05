using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VRCAvatarChanger;

// 絞り込み: フィルタチップ (お気に入りグループ / タグ)、タグ編集、隠し機能の色分け。
public partial class MainWindow
{
    /// <summary>現在のフィルタ値。null = すべて。</summary>
    private string? _filterValue;

    /// <summary>「使えなくなったもの」だけを出すフィルタの値 (タグ名とぶつからないよう記号を含める)。</summary>
    private static readonly string UnavailableFilter = (char)1 + "unavailable"; // 先頭に制御文字 (タグ名には入らない)
    private bool _buildingChips;

    /// <summary>タブに応じてフィルタチップを作り直す。候補が 1 つも無ければバーごと隠す。</summary>
    private void BuildFilterChips()
    {
        _buildingChips = true;
        try
        {
            FilterChips.Children.Clear();
            // お気に入り: VRChat のお気に入りグループ / それ以外: 今の一覧で使われているタグ
            List<string> values = SourceFavorites.IsChecked == true
                ? _allItems.Select(a => a.Avatar.FavoriteGroup).OfType<string>().Where(g => g.Length > 0).Distinct().ToList()
                : _allItems.SelectMany(a => a.Tags)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
            // パブリック: 使えなくなったものがあれば、それだけを見るチップを末尾に足す
            var unavailable = IsPublicTab ? _allItems.Count(a => a.IsUnavailable) : 0;
            if (values.Count == 0 && unavailable == 0)
            {
                FilterBar.Visibility = Visibility.Collapsed;
                _filterValue = null;
                return;
            }
            if (_filterValue is not null && !values.Contains(_filterValue)
                && !(_filterValue == UnavailableFilter && unavailable > 0)) _filterValue = null;

            AddChip("すべて", null);
            foreach (var v in values) AddChip(v, v);
            if (unavailable > 0) AddChip($"使えなくなったもの ({unavailable})", UnavailableFilter);
            FilterBar.Visibility = Visibility.Visible;
            // チップ数が変わってもサイズが変わらない場合があるので、レイアウト後にあふれ判定を行う
            Dispatcher.BeginInvoke(UpdateFilterOverflow, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        finally { _buildingChips = false; }
    }

    private void AddChip(string label, string? value)
    {
        var chip = new RadioButton
        {
            Content = label,
            Tag = value,
            GroupName = "FilterChips",
            Style = (Style)FindResource("SegmentButton"),
            IsChecked = _filterValue == value,
        };
        chip.Checked += FilterChip_Checked;
        FilterChips.Children.Add(chip);
    }

    private void FilterChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_buildingChips) return;
        _filterValue = (sender as RadioButton)?.Tag as string;
        ApplyFilter();
    }

    // ---- 折りたたみ: 閉じた状態はチップ 1 行だけ。あふれたら「もっと見る」 ----

    private bool _filterExpanded;
    private const double FilterRowHeight = 34;      // チップ 1 行分 (MinHeight 30 + 上下マージン 2×2)
    private const double FilterCollapsedMax = FilterRowHeight + 2; // + 枠線

    private void UpdateFilterOverflow()
    {
        // DesiredSize は MaxHeight に丸められるため、チップが 2 行目以降に配置されたかで判定する
        var overflow = FilterChips.Children.OfType<FrameworkElement>()
            .Any(c => c.TranslatePoint(new Point(0, 0), FilterChips).Y > FilterRowHeight / 2);
        FilterChipsHost.MaxHeight = _filterExpanded && overflow ? double.PositiveInfinity : FilterCollapsedMax;
        FilterMoreButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (!overflow) _filterExpanded = false;
        FilterMoreText.Text = _filterExpanded ? "たたむ" : "もっと見る";
        FilterMoreChevron.Text = _filterExpanded ? "\uE70E" : "\uE70D"; // 上 ／ 下シェブロン (Segoe Fluent Icons)
    }

    private void FilterChips_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFilterOverflow();

    private void FilterMore_Click(object sender, RoutedEventArgs e)
    {
        _filterExpanded = !_filterExpanded;
        UpdateFilterOverflow();
    }

    /// <summary>フィルタ選択中ならそれで絞り込む。</summary>
    private IEnumerable<AvatarItem> ApplyChipFilter(IEnumerable<AvatarItem> items)
    {
        if (_filterValue is null) return items;
        if (_filterValue == UnavailableFilter)
            return items.Where(a => a.IsGroup ? a.Members.Any(m => m.IsUnavailable) : a.IsUnavailable);
        if (SourceFavorites.IsChecked == true) return items.Where(a => a.Avatar.FavoriteGroup == _filterValue);
        // タグは、グループタイルの場合「中に 1 体でも該当がいれば」表示する
        return items.Where(a => a.IsGroup
            ? a.Members.Any(m => m.Tags.Contains(_filterValue, StringComparer.CurrentCultureIgnoreCase))
            : a.Tags.Contains(_filterValue, StringComparer.CurrentCultureIgnoreCase));
    }

    private void MenuEditTags_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem item) return;

        if (item.IsGroup)
        {
            // グループ: 全員が共通で持つタグをチェック済みで表示し、保存でメンバー全員に同じタグを適用する
            var common = item.Members
                .Select(m => (IEnumerable<string>)m.Tags)
                .Aggregate((a, b) => a.Intersect(b, StringComparer.CurrentCultureIgnoreCase))
                .ToList();
            var win = new TagPickerWindow($"グループ「{item.Name}」の {item.Members.Count} 体すべてに適用します", _tags.AllTags(), common) { Owner = this };
            if (win.ShowDialog() != true || win.Result is null) return;
            foreach (var m in item.Members)
            {
                _tags.SetTags(m.Id, win.Result);
                m.Tags = win.Result;
            }
            SetStatus(StatusKind.Info, win.Result.Count > 0
                ? $"「{item.Name}」の {item.Members.Count} 体にタグを付けました: {string.Join(", ", win.Result)}"
                : $"「{item.Name}」の {item.Members.Count} 体からタグを外しました");
        }
        else
        {
            var win = new TagPickerWindow(item.Name, _tags.AllTags(), item.Tags) { Owner = this };
            if (win.ShowDialog() != true || win.Result is null) return;
            _tags.SetTags(item.Id, win.Result);
            item.Tags = win.Result;
            SetStatus(StatusKind.Info, win.Result.Count > 0 ? $"{item.Name} のタグ: {string.Join(", ", win.Result)}" : $"{item.Name} のタグを外しました");
        }
        BuildFilterChips();
        ApplyFilter();
    }

    // ---------------- 隠し機能: 10 刻みの色分け (Ctrl+Shift+C) ----------------

    // 6 色ループ。両テーマで薄く乗る程度のアルファ
    private static readonly Brush[] StripePalette =
        new[] { "#4E3E8FD0", "#4E4CAF7D", "#4E9C6ADE", "#4EE09B4C", "#4ED66A9C", "#4E4CB8C4" }
        .Select(c => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return (Brush)b; })
        .ToArray();

    private string StripeKeyOf(AvatarItem item) => item.IsGroup ? "group:" + item.Group!.Id : item.Id;

    private bool IsStripeExcluded(AvatarItem item) => _settings.StripeExcluded.Contains(StripeKeyOf(item));

    /// <summary>
    /// 一覧の下から上に向かって 10 個ごとの色を付ける(末尾が 1 個目)。グループタイルは 1 とカウント。
    /// 除外したものは数えず色も付けない。下から数えるので、新しい順の表示では
    /// アバターが増えても既存のブロックの色が変わらない。
    /// </summary>
    private void ApplyStripes(List<AvatarItem> list)
    {
        if (!_settings.StripeColors)
        {
            foreach (var item in list) item.StripeBrush = null;
            return;
        }
        var count = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if (IsStripeExcluded(item)) { item.StripeBrush = null; continue; }
            item.StripeBrush = StripePalette[count / 10 % StripePalette.Length];
            count++;
        }
    }

    private void ToggleStripeColors()
    {
        _settings.StripeColors = !_settings.StripeColors;
        if (!_preview) _settings.Save();
        ApplyFilter();
        SetStatus(StatusKind.Info, _settings.StripeColors ? "10 体ごとの色分け: オン" : "10 体ごとの色分け: オフ");
    }

    private void MenuStripeExclude_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem item) return;
        var key = StripeKeyOf(item);
        if (!_settings.StripeExcluded.Remove(key)) _settings.StripeExcluded.Add(key);
        if (!_preview) _settings.Save();
        ApplyFilter();
    }
}
