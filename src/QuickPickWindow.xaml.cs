using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VRCAvatarChanger;

/// <summary>
/// クイック着替え: VRChat の上に重ねて画面右からスライドインするアバター選択パネル。
/// 一覧・着替え処理は MainWindow のものを借り、このウィンドウは表示と選択だけを担当する。
/// </summary>
public partial class QuickPickWindow : Window
{
    private readonly Func<AvatarItem, Task<bool>> _change;
    private readonly Action _refocusGame;
    private readonly Action<string> _saveSortKey;
    private List<AvatarItem> _all = [];
    private Dictionary<string, int> _recentRank = [];
    private bool _busy;
    private bool _ready; // 初期化中の SelectionChanged を無視する
    private int _gen; // 閉じアニメ完了時、その後に開き直されていたら Hide しないための世代番号
    private Win32.NativeRect _wantPx; // 置きたい位置とサイズ (物理ピクセル)

    /// <summary>
    /// 設計上の幅 (DIP)。DPI をまたいで移動すると WPF が Width を書き換えてしまうため、
    /// 幅の基準は Width ではなくこの定数から取る。XAML の Width と同じ値にすること。
    /// </summary>
    internal const double DesignWidthDip = 380;

    public QuickPickWindow(Func<AvatarItem, Task<bool>> change, Action refocusGame, Action<string> saveSortKey)
    {
        _change = change;
        _refocusGame = refocusGame;
        _saveSortKey = saveSortKey;
        InitializeComponent();
        Deactivated += (_, _) => { if (!_busy) CloseOverlay(refocus: false); };
        // 初回表示では別の DPI のモニターでウィンドウが作られてから移動するため、移動時に
        // WM_DPICHANGED が起きる。Windows はこのとき DPI 比でサイズを作り直し、直前の
        // SetWindowPos の指定を上書きしてしまう (VRChat の画面外にはみ出す)。置き直して打ち消す。
        DpiChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(ApplyWantedPlacement, DispatcherPriority.Render);
        };
    }

    /// <summary>_wantPx のとおりに実際のウィンドウを置き直す。</summary>
    private void ApplyWantedPlacement()
    {
        if (_wantPx.Right - _wantPx.Left <= 0) return;
        Win32.SetWindowPosPx(Hwnd, _wantPx.Left, _wantPx.Top,
            _wantPx.Right - _wantPx.Left, _wantPx.Bottom - _wantPx.Top);
    }

    public nint Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 領域 (物理ピクセル) の右端に全高で表示し、スライドインする。
    /// PerMonitorV2 の DIP 変換はモニターをまたぐとずれるため、位置決めは SetWindowPos の物理ピクセルで行う。
    /// scale は表示先モニターの DPI スケール (100% = 1.0)。
    /// </summary>
    internal void OpenAt(Win32.NativeRect areaPx, double scale, IReadOnlyList<AvatarItem> items,
        IReadOnlyList<string> recentAvatars, string sortKey, Hotkey toggleKey)
    {
        _gen++;
        CloseHint.Text = toggleKey.IsSet ? $"{toggleKey.Display} / Esc で閉じる" : "Esc で閉じる";
        _all = items.ToList();
        _recentRank = recentAvatars.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);
        _ready = false;
        SortBox.SelectedItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == sortKey) ?? SortBox.Items[0];
        _ready = true;
        SearchBox.Text = "";
        ApplyFilter();
        StatusText.Text = _all.Count == 0 ? "一覧が空です。AvatarChanger でログインして一覧を読み込んでください。" : "";

        var wPx = (int)Math.Round(DesignWidthDip * scale);
        var hPx = areaPx.Bottom - areaPx.Top;
        var xPx = areaPx.Right - wPx;
        _wantPx = new Win32.NativeRect
        {
            Left = xPx, Top = areaPx.Top, Right = xPx + wPx, Bottom = areaPx.Top + hPx,
        };
        Width = DesignWidthDip; // 前回の DPI 変更で書き換わっていることがあるので戻す
        Height = hPx / scale; // WPF のレイアウト用 (物理位置は SetWindowPos が正)

        Slide.X = DesignWidthDip;
        Root.Opacity = 0; // 見えない状態で出してから正しい位置に置く (位置合わせのチラつき防止)
        Show();
        ApplyWantedPlacement();

        var x = new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Slide.BeginAnimation(TranslateTransform.XProperty, x);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        Win32.FocusWindow(Hwnd); // ゲームがフォアグラウンドでも手前に出て検索欄に入力できるように
        Activate();
        SearchBox.Focus();
    }

    public void CloseOverlay(bool refocus)
    {
        if (!IsVisible) return;
        var gen = _gen;
        var x = new DoubleAnimation(DesignWidthDip, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        x.Completed += (_, _) => { if (_gen == gen) Hide(); };
        Slide.BeginAnimation(TranslateTransform.XProperty, x);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)));
        if (refocus) _refocusGame();
    }

    private string SortKey => (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "recent";

    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _saveSortKey(SortKey);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        IEnumerable<AvatarItem> items = q.Length == 0
            ? _all
            : _all.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || a.AuthorName.Contains(q, StringComparison.OrdinalIgnoreCase));
        // 「最近使用した順」は使用履歴の順。未使用のものは元の並びのまま後ろに置く (OrderBy は安定ソート)
        var sorted = SortKey == "recent"
            ? items.OrderBy(a => _recentRank.TryGetValue(a.Id, out var r) ? r : int.MaxValue).ToList()
            : MainWindow.ApplySort(items, SortKey).ToList();
        List.ItemsSource = sorted;
        var current = sorted.FindIndex(a => a.IsCurrent);
        List.SelectedIndex = current >= 0 ? current : (sorted.Count > 0 ? 0 : -1);
        if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private async void List_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(List, src) is not ListBoxItem { Content: AvatarItem item }) return;
        await ChangeAsync(item);
    }

    private async Task ChangeAsync(AvatarItem item)
    {
        if (_busy) return;
        _busy = true;
        StatusText.Text = $"{item.Name} に着替えています...";
        var ok = false;
        try { ok = await _change(item); }
        finally { _busy = false; }
        if (ok) CloseOverlay(refocus: true);
        else StatusText.Text = "着替えられませんでした。メイン画面のステータスを確認してください。";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseOverlay(refocus: true);
        }
        else if (e.Key == Key.Enter && List.SelectedItem is AvatarItem item)
        {
            e.Handled = true;
            _ = ChangeAsync(item);
        }
        else if (e.Key is Key.Down or Key.Up && SearchBox.IsKeyboardFocused && List.Items.Count > 0)
        {
            // 検索欄にフォーカスを残したまま矢印キーで選択を動かせるようにする
            e.Handled = true;
            var next = List.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
            List.SelectedIndex = Math.Clamp(next, 0, List.Items.Count - 1);
            if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseOverlay(refocus: true);
}
