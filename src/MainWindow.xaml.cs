using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

/// <summary>
/// 一覧の 1 要素。アバター 1 体、または複数のアバターをまとめたグループ(代表アバターのサムネで表示)。
/// </summary>
public sealed class AvatarItem : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private bool _isDropTarget;

    /// <summary>アバター(グループの場合は代表 = 先頭メンバー)。</summary>
    public Avatar Avatar { get; }
    /// <summary>パブリックリストに追加した日時(パブリックタブのみ)。並び順の「追加日」に使う。</summary>
    public DateTimeOffset? AddedAt { get; init; }
    private IReadOnlyList<string> _tags = [];
    /// <summary>ユーザーが付けたタグ(自分のアバター / パブリックタブ)。</summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set { _tags = value; OnPropertyChanged(); OnPropertyChanged(nameof(Badge)); }
    }

    public AvatarGroup? Group { get; }
    public IReadOnlyList<AvatarItem> Members { get; } = [];
    /// <summary>グループの代表メンバー(一番古いもの)。</summary>
    public AvatarItem? Representative { get; }
    public bool IsGroup => Group is not null;
    public bool IsAvatar => Group is null;
    public int Count => IsGroup ? Members.Count : 1;

    public AvatarItem(Avatar avatar) { Avatar = avatar; }

    // 弱イベントの購読を保持する(タイル自身が生きている間だけ通知を受け、破棄されたら購読ごと回収される)
    private readonly EventHandler<PropertyChangedEventArgs>? _repThumbnailHandler;

    public AvatarItem(AvatarGroup group, IReadOnlyList<AvatarItem> members)
    {
        Group = group;
        Members = members;
        // 代表は「一番古い」メンバー(追加日 / 作成日が最も古いもの)。並び順もサムネもこれに従う
        var rep = members
            .OrderBy(m => m.AddedAt ?? m.Avatar.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(m => m.Avatar.UpdatedAt ?? DateTimeOffset.MaxValue)
            .First();
        Representative = rep;
        Avatar = rep.Avatar;
        AddedAt = rep.AddedAt;
        // 代表のサムネが後から届いたら自分の表示も更新する。
        // グループタイルは絞り込みのたびに作り直されるため、通常の += だと古いタイルへの
        // 購読が代表メンバー側に溜まり続ける。弱イベントで購読して自然に回収させる
        _repThumbnailHandler = (_, e) => { if (e.PropertyName == nameof(Thumbnail)) OnPropertyChanged(nameof(Thumbnail)); };
        PropertyChangedEventManager.AddHandler(rep, _repThumbnailHandler, nameof(Thumbnail));
    }

    public string Id => Avatar.Id;
    public string Name => Group?.Name ?? Avatar.Name;
    public string AuthorName => IsGroup ? $"{Members.Count} 体" : Avatar.AuthorName;
    public string Badge => IsGroup ? "グループ"
        : Tags.Count > 0 ? string.Join(", ", Tags)
        : !string.IsNullOrEmpty(Avatar.FavoriteGroup) ? Avatar.FavoriteGroup
        : Avatar.ReleaseStatus == "private" ? "非公開" : Avatar.ReleaseStatus == "public" ? "公開" : "";
    public string? ThumbnailUrl => Avatar.ThumbnailImageUrl ?? Avatar.ImageUrl;

    public BitmapImage? Thumbnail
    {
        get => IsGroup ? Representative!.Thumbnail : _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    private Brush? _stripeBrush;
    /// <summary>10 刻み色分け(隠し機能)の背景色。無効時や除外時は null。</summary>
    public Brush? StripeBrush
    {
        get => _stripeBrush;
        set { if (!ReferenceEquals(_stripeBrush, value)) { _stripeBrush = value; OnPropertyChanged(); } }
    }

    private bool _isCurrent;
    /// <summary>現在着ているアバター(グループの場合は中に現在のアバターがいる)。チェックバッジの表示に使う。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent != value) { _isCurrent = value; OnPropertyChanged(); } }
    }

    /// <summary>ドラッグ中に重ねられているとき true(見た目のハイライト用)。</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget != value) { _isDropTarget = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>幅から 4:3 の高さを作る。ConverterParameter="clip" の場合は角丸クリップ用の RectangleGeometry を返す。</summary>
public sealed class AspectHeightConverter : IValueConverter, IMultiValueConverter
{
    public double Ratio { get; set; } = 0.75;

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is double w && !double.IsNaN(w) ? Math.Max(0, w * Ratio) : 0d;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();

    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var w = values.Length > 0 && values[0] is double a && !double.IsNaN(a) ? a : 0;
        var h = values.Length > 1 && values[1] is double b && !double.IsNaN(b) ? b : 0;
        var geo = new RectangleGeometry(new Rect(0, 0, w, h), 6, 6);
        geo.Freeze();
        return geo;
    }
}

public partial class MainWindow : Window
{
    public static readonly DependencyProperty GridColumnsProperty =
        DependencyProperty.Register(nameof(GridColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(5));

    /// <summary>ボックス表示のときの 1 行あたりの数。ItemsPanel の UniformGrid が参照する。</summary>
    public int GridColumns
    {
        get => (int)GetValue(GridColumnsProperty);
        set => SetValue(GridColumnsProperty, value);
    }

    private readonly Settings _settings = Settings.Load();
    private readonly PublicAvatarStore _public = PublicAvatarStore.Load();
    private readonly GroupStore _groups = GroupStore.Load();
    private readonly TagStore _tags = TagStore.Load();
    private bool _preview = false;
    private readonly bool _ready; // InitializeComponent 中に飛ぶ Checked / ValueChanged を無視するため
    private readonly VRChatApi _api = new();
    private readonly List<AvatarItem> _allItems = [];
    private readonly Dictionary<string, BitmapImage> _imageCache = [];
    private CurrentUser? _user;
    private IReadOnlyList<string> _twoFactorMethods = [];
    private CancellationTokenSource? _thumbCts;
    private readonly OscListener _osc = new();
    private readonly Dictionary<string, Avatar> _avatarInfoCache = [];

    public MainWindow()
    {
        // 旧バージョンではタグを public_avatars.json 内に保存していた。tags.json へ一度だけ移行する
        var migrated = false;
        foreach (var entry in _public.Entries.Where(en => en.Tags.Count > 0))
        {
            if (_tags.TagsOf(entry.Avatar.Id).Count == 0) _tags.SetTags(entry.Avatar.Id, entry.Tags);
            entry.Tags.Clear();
            migrated = true;
        }
        if (migrated) _public.Save();

        var savedColumns = _settings.GridColumns;
        var savedView = _settings.ViewMode;
        var savedSort = _settings.SortKey;
        InitializeComponent();
        SortBox.SelectedItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == savedSort) ?? SortBox.Items[0];
        _ready = true;
        GridColumns = savedColumns;
        ColumnsSlider.Value = savedColumns;
        ColumnsLabel.Text = $"{savedColumns} 列";
        (savedView == "grid" ? ViewGrid : ViewList).IsChecked = true;
        GroupToggle.IsChecked = _settings.GroupView;
        ApplyPanels();
        _osc.AvatarChanged += id => Dispatcher.BeginInvoke(() => OnOscAvatarChanged(id));
        RestoreWindowBounds();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        Loaded += async (_, _) => await TryRestoreSessionAsync();
        Loaded += async (_, _) => await CheckForUpdateAsync();
        Closing += (_, _) => SaveWindowBounds();
        Closed += (_, _) => { _settingsSaveTimer?.Stop(); _searchTimer?.Stop(); _oscRetry?.Stop(); _thumbCts?.Cancel(); _osc.Dispose(); _api.Dispose(); };
#if DEBUG
        // UI 確認用: 環境変数 VRCAC_UI_PREVIEW=1 で API を叩かずにダミーデータでメイン画面を表示する (Debug ビルドのみ)
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW") == "1")
        {
            _preview = true;
            Loaded += (_, _) => ShowUiPreview();
        }
#endif
    }

#if DEBUG
    private void ShowUiPreview()
    {
        _user = new CurrentUser { DisplayName = "Preview User", CurrentAvatar = "avtr_00000000-0000-4000-8000-000000000001" };
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        _allItems.Clear();
        string[] names = ["Kikyo", "Selestia", "Manuka", "Shinano", "Rurune", "Moe", "Lime", "Mizuki"];
        // VRCAC_UI_PREVIEW_COUNT=500 のように件数を増やして、仮想化やスクロールの負荷を確認できる
        var count = int.TryParse(Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_COUNT"), out var c) ? c : names.Length;
        for (var i = 0; i < count; i++)
            _allItems.Add(new AvatarItem(new Avatar
            {
                Id = $"avtr_00000000-0000-4000-8000-{i + 1:D12}",
                Name = names[i % names.Length] + (i >= names.Length ? $" {i + 1}" : "") + (i % 3 == 0 ? " (改変)" : ""),
                AuthorName = "preview_author",
                ReleaseStatus = i % 2 == 0 ? "private" : "public",
                CreatedAt = DateTimeOffset.Now.AddDays(-i * 3),
                UpdatedAt = DateTimeOffset.Now.AddDays(-i),
            }));
        ShowListState(loading: Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_STATE") == "loading");
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_STATE") == "empty") _allItems.Clear();
        ApplyFilter();
        AvatarList.SelectedIndex = 1;
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_VIEW") == "grid") ViewGrid.IsChecked = true;
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SOURCE") == "public") SourcePublic.IsChecked = true;
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_TOPMOST") == "1") { Topmost = true; Activate(); }
        // ドロップ処理の確認: 一覧の 2 番目を 3 番目に重ね、続けて 1 番目をできたグループに重ねる
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_DROP") == "1")
        {
            var items = AvatarList.Items.OfType<AvatarItem>().ToList();
            if (items.Count >= 3)
            {
                PerformDrop(items[1], items[2]);
                items = AvatarList.Items.OfType<AvatarItem>().ToList();
                var group = items.First(a => a.IsGroup);
                PerformDrop(items.First(a => a.IsAvatar), group);
            }
        }
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_OPEN") == "1" && _groups.Groups.Count > 0) OpenGroup(_groups.Groups[0]);
        UpdateUserHeader();
        OscStatusText.Text = "OSC 連携中";
        SetStatus(StatusKind.Success, "Kikyo に着替えました");
        // 仮想化の自己診断: スクロールしながら実体化済みコンテナ数を VRCAC_UI_PREVIEW_REPORT のファイルに書いて終了する
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SCROLLTEST") == "1") _ = RunScrollTestAsync();
    }

    private async Task RunScrollTestAsync()
    {
        Left = -4000; Top = 0; // 検証用: 画面外に出して作業の邪魔をしない
        var reportPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_REPORT");
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, "started\r\n");
        var report = new System.Text.StringBuilder();
        try
        {
            await Task.Delay(1200);
            var sv = FindDescendant<ScrollViewer>(AvatarList);
            void Snapshot(string label)
            {
                var realized = CountDescendants<ListViewItem>(AvatarList);
                report.AppendLine($"{label}: items={AvatarList.Items.Count} realized={realized} " +
                    $"offset={sv?.VerticalOffset:F0} extent={sv?.ExtentHeight:F0} viewport={sv?.ViewportHeight:F0}");
            }
            Snapshot("top");
            foreach (var (label, ratio) in new[] { ("middle", 0.5), ("bottom", 1.0), ("back-to-top", 0.0) })
            {
                sv?.ScrollToVerticalOffset((sv.ExtentHeight - sv.ViewportHeight) * ratio);
                await Task.Delay(400);
                Snapshot(label);
            }
            // VR スティック風のホイール連続入力。SteamVR の実挙動に合わせて間隔を揺らし、約 50ms ごとの移動量で等速性を見る
            void RaiseWheel() => AvatarList.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = AvatarList });
            var samples = new List<double>();
            foreach (var gap in new[] { 200, 90, 260, 120, 230, 80, 250, 150 })
            {
                RaiseWheel();
                for (var elapsed = 0; elapsed < gap; elapsed += 50)
                {
                    await Task.Delay(Math.Min(50, gap - elapsed));
                    samples.Add(sv?.VerticalOffset ?? -1);
                }
            }
            await Task.Delay(800);
            samples.Add(sv?.VerticalOffset ?? -1);
            var deltas = samples.Zip(samples.Skip(1), (a, b) => b - a).Select(d => d.ToString("F0"));
            report.AppendLine("wheel offsets: " + string.Join(", ", samples.Select(o => o.ToString("F0"))));
            report.AppendLine("wheel deltas/50ms: " + string.Join(", ", deltas));

            // リサイクル後のコンテナ整合性: Content が今の項目を指しているか / 重複が無いか
            var containers = new List<ListViewItem>();
            CollectDescendants(AvatarList, containers);
            var bad = containers.Count(c => c.Content is not AvatarItem || !ReferenceEquals(c.Content, c.DataContext));
            var dup = containers.Count - containers.Select(c => c.Content).Distinct().Count();
            report.AppendLine($"container integrity: total={containers.Count} bad={bad} duplicates={dup}");
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private static int CountDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var n = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) n++;
            n += CountDescendants<T>(child);
        }
        return n;
    }

    private static void CollectDescendants<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) into.Add(t);
            CollectDescendants(child, into);
        }
    }
#endif

    // ---------------- 表示形式 ----------------

    private bool IsGridView => ViewGrid.IsChecked == true;

    private void ViewMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var grid = IsGridView;
        AvatarList.ItemContainerStyle = (Style)FindResource(grid ? "AvatarTile" : "AvatarRow");
        AvatarList.ItemTemplate = (DataTemplate)FindResource(grid ? "AvatarTileTemplate" : "AvatarRowTemplate");
        ColumnsPanel.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        ApplyPanels();
        _settings.ViewMode = grid ? "grid" : "list";
        if (!_preview) _settings.Save();
        if (AvatarList.SelectedItem is not null) AvatarList.ScrollIntoView(AvatarList.SelectedItem);
    }

    private void ApplyPanels()
        => AvatarList.ItemsPanel = (ItemsPanelTemplate)FindResource(IsGridView ? "GridPanel" : "ListPanel");

    private System.Windows.Threading.DispatcherTimer? _settingsSaveTimer;

    /// <summary>スライダーのドラッグ中など、連続で変わる値の保存をまとめる(500ms 静止後に 1 回だけ書く)。</summary>
    private void SaveSettingsDebounced()
    {
        if (_preview) return;
        if (_settingsSaveTimer is null)
        {
            _settingsSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer!.Stop(); _settings.Save(); };
        }
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void ColumnsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var n = (int)Math.Round(e.NewValue);
        GridColumns = n;
        ColumnsLabel.Text = $"{n} 列";
        _settings.GridColumns = n;
        SaveSettingsDebounced();
    }

    // ---------------- 並び順 ----------------

    private string SortKey => (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "created_desc";

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.SortKey = SortKey;
        if (!_preview) _settings.Save();
        ApplyFilter();
    }

    private static IEnumerable<AvatarItem> ApplySort(IEnumerable<AvatarItem> items, string key)
    {
        var desc = key.EndsWith("_desc", StringComparison.Ordinal);
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        // 「追加日」はパブリックタブではリストに追加した日時、それ以外はアバターの作成日時。日時が無いものは末尾に寄せる
        static DateTimeOffset? Added(AvatarItem a) => a.AddedAt ?? a.Avatar.CreatedAt;
        return key switch
        {
            "created_asc" or "created_desc" => desc
                ? items.OrderByDescending(a => Added(a) ?? DateTimeOffset.MinValue).ThenBy(a => a.Name, cmp)
                : items.OrderBy(a => Added(a) ?? DateTimeOffset.MaxValue).ThenBy(a => a.Name, cmp),
            "updated_asc" or "updated_desc" => desc
                ? items.OrderByDescending(a => a.Avatar.UpdatedAt ?? DateTimeOffset.MinValue).ThenBy(a => a.Name, cmp)
                : items.OrderBy(a => a.Avatar.UpdatedAt ?? DateTimeOffset.MaxValue).ThenBy(a => a.Name, cmp),
            "name_desc" => items.OrderByDescending(a => a.Name, cmp),
            _ => items.OrderBy(a => a.Name, cmp),
        };
    }

    // ---------------- パブリックリスト ----------------

    /// <summary>入力から avtr_ ID を取り出す。URL (https://vrchat.com/home/avatar/avtr_...) も可。</summary>
    private static string? ExtractAvatarId(string input)
    {
        var s = input.Trim();
        var idx = s.IndexOf("avtr_", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[idx..].Split('?', '/', '#', ' ')[0];
        return VRChatApi.IsValidAvatarId(s) ? s : null;
    }

    private void PublicIdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PublicAdd_Click(sender, e);
    }

    private async void PublicAdd_Click(object sender, RoutedEventArgs e)
    {
        var id = ExtractAvatarId(PublicIdBox.Text);
        if (id is null)
        {
            SetStatus(StatusKind.Error, "avtr_ で始まるアバター ID か、アバターページの URL を入力してください。");
            return;
        }
        PublicAddButton.IsEnabled = false;
        try
        {
            SetStatus(StatusKind.Info, "アバター情報を取得しています");
            var av = await _api.GetAvatarAsync(id);
            if (await TryAddPublicAsync(av)) PublicIdBox.Clear();
        }
        catch (Exception ex) { SetStatus(StatusKind.Error, "アバター情報を取得できませんでした: " + FriendlyError.Of(ex)); }
        finally { PublicAddButton.IsEnabled = true; }
    }

    /// <summary>パブリックリストに追加。他人の非公開アバターは着替えられないので拒否する。</summary>
    private async Task<bool> TryAddPublicAsync(Avatar av)
    {
        if (av.ReleaseStatus != "public")
        {
            SetStatus(StatusKind.Error, $"{av.Name} は非公開アバターのため追加できません");
            return false;
        }
        var added = _public.Add(av);
        SetStatus(added ? StatusKind.Success : StatusKind.Info, added ? $"{av.Name} をパブリックに追加しました" : $"{av.Name} はすでにパブリックにあります");
        if (IsPublicTab) await LoadAvatarsAsync();
        return added;
    }

    private async Task RefreshPublicEntriesAsync(CancellationToken ct)
    {
        // 件数が多くてもレート制限にかからないよう、同時 2 本まで
        using var gate = new SemaphoreSlim(2);
        var tasks = _public.Entries.ToList().Select(async e =>
        {
            await gate.WaitAsync(ct);
            try { _public.Update(await _api.GetAvatarAsync(e.Avatar.Id, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* 削除された・非公開になったアバターはそのまま残す */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        _public.Save();
    }

    private void AvatarList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右クリックした行を選択してからメニューを出す
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null && dep is not ListViewItem) dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListViewItem item) item.IsSelected = true;
    }

    private void AvatarList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem item) { e.Handled = true; return; }
        var isGroup = item.IsGroup;
        var pub = IsPublicTab;
        var isPublicAvatar = item.Avatar.ReleaseStatus == "public";
        var canAdd = !pub && !isGroup && isPublicAvatar && !_public.Contains(item.Id);

        MenuChange.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;
        MenuOpenGroup.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;

        MenuAssignGroup.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;
        MenuAssignGroup.Header = _openGroup is null ? "グループに入れる..." : "別のグループに移す...";
        MenuUnassignGroup.Visibility = !isGroup && _openGroup is not null ? Visibility.Visible : Visibility.Collapsed;
        MenuRenameGroup.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        MenuDissolveGroup.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;

        // パブリックリストに入れられるのは公開アバターだけ。非公開には項目自体を出さない
        MenuAddPublic.Visibility = pub || isGroup || !isPublicAvatar ? Visibility.Collapsed : Visibility.Visible;
        MenuAddPublic.IsEnabled = canAdd;
        MenuAddPublic.Header = _public.Contains(item.Id) ? "パブリックに追加済み" : "パブリックに追加";
        MenuRemovePublic.Visibility = pub && !isGroup ? Visibility.Visible : Visibility.Collapsed;
        // タグは「自分のアバター」と「パブリック」で使える。グループならメンバー全員にまとめて適用
        MenuEditTags.Visibility = pub || SourceOwn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MenuEditTags.Header = isGroup ? "全員のタグを編集..." : "タグを編集...";
        MenuCopyId.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;

        // 隠し機能が有効なときだけ出す
        MenuStripeExclude.Visibility = _settings.StripeColors ? Visibility.Visible : Visibility.Collapsed;
        MenuStripeExclude.Header = IsStripeExcluded(item) ? "カウントに戻す" : "カウントから除外";
    }

    // ---------------- フィルタ (お気に入りグループ / タグ) ----------------

    /// <summary>現在のフィルタ値。null = すべて。</summary>
    private string? _filterValue;
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
            if (values.Count == 0)
            {
                FilterBar.Visibility = Visibility.Collapsed;
                _filterValue = null;
                return;
            }
            if (_filterValue is not null && !values.Contains(_filterValue)) _filterValue = null;

            AddChip("すべて", null);
            foreach (var v in values) AddChip(v, v);
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
        FilterMoreChevron.Text = _filterExpanded ? "" : ""; // 上 ／ 下シェブロン
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

    // ---------------- グループ ----------------

    private AvatarGroup? _openGroup;

    /// <summary>ON: グループを 1 枚にまとめて表示 / OFF: すべて個別に表示。</summary>
    private bool GroupViewOn => GroupToggle.IsChecked == true;

    private void GroupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // OFF にしたら、開いているグループ表示も解除して通常一覧に戻す
        if (!GroupViewOn && _openGroup is not null)
        {
            _openGroup = null;
            GroupBar.Visibility = Visibility.Collapsed;
        }
        _settings.GroupView = GroupViewOn;
        if (!_preview) _settings.Save();
        ApplyFilter();
    }

    private void OpenGroup(AvatarGroup group)
    {
        _openGroup = group;
        GroupBar.Visibility = Visibility.Visible;
        ApplyFilter();
    }

    private void CloseGroup()
    {
        var g = _openGroup;
        _openGroup = null;
        GroupBar.Visibility = Visibility.Collapsed;
        ApplyFilter();
        // 戻ったら開いていたグループのタイルを選択しておく
        if (g is not null && AvatarList.Items.OfType<AvatarItem>().FirstOrDefault(a => a.Group == g) is { } tile)
        {
            AvatarList.SelectedItem = tile;
            AvatarList.ScrollIntoView(tile);
        }
    }

    private void GroupBack_Click(object sender, RoutedEventArgs e) => CloseGroup();

    private void MenuOpenGroup_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { Group: { } g }) OpenGroup(g);
    }

    /// <summary>グループの中身が 1 体以下になったら、グループとしての意味がないので解除する。</summary>
    private void PruneGroup(AvatarGroup group)
    {
        if (group.AvatarIds.Count <= 1)
        {
            _groups.Delete(group);
            if (_openGroup == group) { _openGroup = null; GroupBar.Visibility = Visibility.Collapsed; }
        }
    }

    private void MenuAssignGroup_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem { IsAvatar: true } item) return;
        var win = new GroupPickerWindow(_groups, item.Name, _groups.GroupOf(item.Id)) { Owner = this };
        if (win.ShowDialog() == true && win.Result is not null)
        {
            var from = _groups.GroupOf(item.Id);
            _groups.Assign(item.Id, win.Result);
            if (from is not null && from != win.Result) PruneGroup(from);
            SetStatus(StatusKind.Success, $"{item.Name} を「{win.Result.Name}」に入れました");
            ApplyFilter();
        }
    }

    private void MenuUnassignGroup_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem { IsAvatar: true } item) return;
        var from = _groups.GroupOf(item.Id);
        _groups.Unassign(item.Id);
        if (from is not null) PruneGroup(from);
        SetStatus(StatusKind.Info, $"{item.Name} をグループから外しました");
        ApplyFilter();
    }

    private void MenuRenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem { Group: { } group }) return;
        var win = new GroupPickerWindow(_groups, group) { Owner = this };
        if (win.ShowDialog() == true && win.NewName is { } newName && newName != group.Name)
        {
            var dup = _groups.FindByName(newName);
            if (dup is not null && dup != group)
            {
                SetStatus(StatusKind.Error, $"「{newName}」という名前のグループはすでにあります");
                return;
            }
            _groups.Rename(group, newName);
            ApplyFilter();
        }
    }

    private void MenuDissolveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem { Group: { } group }) return;
        _groups.Delete(group);
        SetStatus(StatusKind.Info, $"グループ「{group.Name}」を解除しました");
        ApplyFilter();
    }

    // ---------------- ドラッグ&ドロップでグループ化 ----------------

    private Point _dragStart;
    private AvatarItem? _dragSource;
    private AvatarItem? _dropTarget;

    private static AvatarItem? ItemAt(ItemsControl list, DependencyObject? origin)
    {
        while (origin is not null && origin is not ListViewItem) origin = VisualTreeHelper.GetParent(origin);
        return origin is ListViewItem lvi ? lvi.DataContext as AvatarItem : null;
    }

    private void AvatarList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(AvatarList);
        _dragSource = ItemAt(AvatarList, e.OriginalSource as DependencyObject);
    }

    private void AvatarList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(AvatarList) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        // グループを開いている間は並べ替えの意味しかないのでドラッグしない
        if (_openGroup is not null) { _dragSource = null; return; }
        var src = _dragSource;
        _dragSource = null;
        DragDrop.DoDragDrop(AvatarList, new DataObject(typeof(AvatarItem), src), DragDropEffects.Move);
        SetDropTarget(null);
    }

    private void SetDropTarget(AvatarItem? target)
    {
        if (_dropTarget == target) return;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = target;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = true;
    }

    private void AvatarList_DragOver(object sender, DragEventArgs e)
    {
        var src = e.Data.GetData(typeof(AvatarItem)) as AvatarItem;
        var target = ItemAt(AvatarList, e.OriginalSource as DependencyObject);
        var ok = src is not null && target is not null && !ReferenceEquals(src, target) && !(src.IsGroup && target.IsGroup && src.Group == target.Group);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(ok ? target : null);
        e.Handled = true;
    }

    private void AvatarList_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void AvatarList_Drop(object sender, DragEventArgs e)
    {
        var src = e.Data.GetData(typeof(AvatarItem)) as AvatarItem;
        var target = ItemAt(AvatarList, e.OriginalSource as DependencyObject);
        SetDropTarget(null);
        e.Handled = true;
        if (src is null || target is null || ReferenceEquals(src, target)) return;
        PerformDrop(src, target);
    }

    /// <summary>src を target に重ねたときのグループ化。</summary>
    private void PerformDrop(AvatarItem src, AvatarItem target)
    {
        // 移動元のアバター ID 群
        var srcIds = src.IsGroup ? src.Members.Select(m => m.Id).ToList() : [src.Id];
        var srcGroup = src.Group;

        AvatarGroup dest;
        string message;
        if (target.IsGroup)
        {
            dest = target.Group!;
            message = src.IsGroup
                ? $"「{src.Name}」を「{dest.Name}」に統合しました"
                : $"{src.Name} を「{dest.Name}」に入れました";
        }
        else
        {
            // アバターに重ねた: そのアバター名でグループを作る(同名があればそこへ)
            dest = _groups.FindByName(target.Name) ?? _groups.Create(target.Name);
            _groups.Assign(target.Id, dest);
            message = src.IsGroup
                ? $"「{src.Name}」と {target.Name} を「{dest.Name}」にまとめました"
                : $"{src.Name} と {target.Name} を「{dest.Name}」にまとめました。名前は右クリックで変えられます";
        }
        foreach (var id in srcIds) _groups.Assign(id, dest);
        if (srcGroup is not null && srcGroup != dest) _groups.Delete(srcGroup);

        SetStatus(StatusKind.Success, message);
        ApplyFilter();
        if (AvatarList.Items.OfType<AvatarItem>().FirstOrDefault(a => a.Group == dest) is { } tile) AvatarList.SelectedItem = tile;
    }

    private async void MenuChange_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) await ChangeAvatarAsync(item.Id, item.Name);
    }

    private async void MenuAddPublic_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) await TryAddPublicAsync(item.Avatar);
    }

    private async void MenuRemovePublic_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is not AvatarItem { IsAvatar: true } item) return;
        _public.Remove(item.Id);
        SetStatus(StatusKind.Info, $"{item.Name} をパブリックから削除しました");
        await LoadAvatarsAsync();
    }

    private void MenuCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item)
        {
            try { Clipboard.SetText(item.Id); SetStatus(StatusKind.Info, "アバター ID をコピーしました"); } catch { }
        }
    }

    // ---------------- 状態表示 ----------------

    private enum StatusKind { Info, Success, Error }

    private void SetStatus(StatusKind kind, string text)
    {
        StatusText.Text = text;
        switch (kind)
        {
            case StatusKind.Success:
                StatusIcon.Text = "\uE73E"; // Segoe Fluent Icons: CheckMark
                StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
                StatusIcon.Visibility = Visibility.Visible;
                break;
            case StatusKind.Error:
                StatusIcon.Text = "\uEA39"; // Segoe Fluent Icons: ErrorBadge
                StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                StatusIcon.Visibility = Visibility.Visible;
                break;
            default:
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
                StatusIcon.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ShowListState(bool loading)
    {
        SkeletonPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        AvatarList.Visibility = loading ? Visibility.Hidden : Visibility.Visible;
        if (loading) EmptyPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyState(int shownCount)
    {
        if (shownCount > 0) { EmptyPanel.Visibility = Visibility.Collapsed; return; }
        var filtering = SearchBox.Text.Trim().Length > 0 || _filterValue is not null;
        if (filtering)
        {
            EmptyTitle.Text = "一致するアバターがありません";
            EmptyHint.Text = _filterValue is not null
                ? $"「{_filterValue}」に一致するアバターがありません。「すべて」に戻すか、別のフィルタを試してください。"
                : "別の名前や作者名、ID で試してください。";
        }
        else if (SourceFavorites.IsChecked == true)
        {
            EmptyTitle.Text = "お気に入りのアバターがありません";
            EmptyHint.Text = "VRChat でお気に入りに登録したアバターがここに表示されます。";
        }
        else if (SourcePublic.IsChecked == true)
        {
            EmptyTitle.Text = "パブリックアバターはまだ登録されていません";
            EmptyHint.Text = "上の欄にアバターの ID か URL を貼って「追加」するか、他のタブで右クリック →「パブリックに追加」。上限はありません。";
        }
        else
        {
            EmptyTitle.Text = "アップロードしたアバターがありません";
            EmptyHint.Text = "自分でアップロードしたアバターがここに表示されます。「お気に入り」タブも試してみてください。";
        }
        EmptyPanel.Visibility = Visibility.Visible;
    }

    // ---------------- OSC ----------------

    private System.Windows.Threading.DispatcherTimer? _oscRetry;

    /// <summary>OSC の待ち受けを開始。ポートが取れない場合はユーザーには何も見せず、30 秒ごとに静かに再試行する。</summary>
    private void StartOsc()
    {
        try
        {
            _osc.Start();
            OscStatusText.Text = "OSC 連携中";
            OscStatusText.Visibility = Visibility.Visible;
            _oscRetry?.Stop();
            _oscRetry = null;
        }
        catch
        {
            OscStatusText.Visibility = Visibility.Collapsed;
            if (_oscRetry is null)
            {
                _oscRetry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _oscRetry.Tick += (_, _) => { if (_user is not null && !_osc.IsListening) StartOsc(); };
                _oscRetry.Start();
            }
        }
    }

    private void OnOscAvatarChanged(string avatarId)
    {
        if (_user is null || _user.CurrentAvatar == avatarId) return;
        _user.CurrentAvatar = avatarId;
        _user.CurrentAvatarThumbnailImageUrl = null; // 旧アバターのサムネなので破棄
        // 一覧に無いアバターの名前・サムネは UpdateUserHeader 内 (ResolveCurrentAvatarAsync) が API から引く
        UpdateUserHeader();
        OscStatusText.Text = $"OSC 連携中 ({DateTime.Now:HH:mm} にゲーム内の着替えを検知)";
    }

    // ---------------- 認証 ----------------

    private async Task TryRestoreSessionAsync()
    {
        if (!_api.HasSavedSession) return;
        SetLoginBusy(true, "保存されたセッションを確認中...");
        try
        {
            var user = await _api.TryGetCurrentUserAsync();
            if (user is not null) await EnterMainAsync(user);
            else LoginStatus.Text = "";
        }
        catch (Exception ex) { SetLoginStatus(FriendlyError.Of(ex), error: true); }
        finally { SetLoginBusy(false); }
    }

    private void LoginField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoginButton_Click(sender, e);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text.Trim();
        var pass = PasswordBox.Password;
        if (user.Length == 0 || pass.Length == 0)
        {
            SetLoginStatus("ユーザー名とパスワードを入力してください", error: true);
            return;
        }
        SetLoginBusy(true, "ログイン中...");
        try
        {
            var me = await _api.LoginAsync(user, pass);
            PasswordBox.Clear();
            await EnterMainAsync(me);
        }
        catch (TwoFactorRequiredException tfa)
        {
            PasswordBox.Clear();
            _twoFactorMethods = tfa.Methods;
            ShowTwoFactor();
        }
        catch (Exception ex) { SetLoginStatus(FriendlyError.Of(ex), error: true); }
        finally { SetLoginBusy(false); }
    }

    private async void BrowserLoginButton_Click(object sender, RoutedEventArgs e)
    {
        LoginStatus.Text = "";
        var win = new BrowserLoginWindow(_api) { Owner = this };
        var ok = win.ShowDialog() == true && win.Result is not null;
        if (ok) await EnterMainAsync(win.Result!);
        else SetLoginStatus("ブラウザでのログインを中止しました", error: false);
    }

    private void ShowTwoFactor()
    {
        CredentialsPanel.Visibility = Visibility.Collapsed;
        TwoFactorPanel.Visibility = Visibility.Visible;
        TwoFactorHint.Text = _twoFactorMethods.Contains("totp")
            ? "認証アプリに表示されている 6 桁のコードを入力してください(リカバリーコードも可)。"
            : "VRChat に登録したメールアドレスに届いた 6 桁のコードを入力してください。";
        LoginStatus.Text = "";
        TwoFactorCodeBox.Clear();
        TwoFactorCodeBox.Focus();
    }

    private void BackToLogin_Click(object sender, RoutedEventArgs e)
    {
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Visible;
        LoginStatus.Text = "";
    }

    private void TwoFactorField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) VerifyButton_Click(sender, e);
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        var code = TwoFactorCodeBox.Text.Trim();
        if (code.Length == 0) return;
        SetLoginBusy(true, "認証中...");
        try
        {
            string method;
            if (_twoFactorMethods.Contains("emailOtp")) method = "emailOtp";
            // 8桁英数字ならリカバリーコード(otp)、それ以外は TOTP
            else if (code.Length == 8 && _twoFactorMethods.Contains("otp") && !code.All(char.IsDigit)) method = "otp";
            else method = "totp";

            var me = await _api.VerifyTwoFactorAsync(method, code);
            TwoFactorPanel.Visibility = Visibility.Collapsed;
            CredentialsPanel.Visibility = Visibility.Visible;
            await EnterMainAsync(me);
        }
        catch (Exception ex) { SetLoginStatus(FriendlyError.Of(ex), error: true); }
        finally { SetLoginBusy(false); }
    }

    private void SetLoginBusy(bool busy, string? status = null)
    {
        LoginButton.IsEnabled = !busy;
        BrowserLoginButton.IsEnabled = !busy;
        VerifyButton.IsEnabled = !busy;
        if (status is not null) SetLoginStatus(status, error: false);
    }

    private void SetLoginStatus(string text, bool error)
    {
        LoginStatus.Text = text;
        LoginStatus.Foreground = (System.Windows.Media.Brush)FindResource(error ? "DangerBrush" : "MutedTextBrush");
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _api.LogoutAsync();
        BrowserLoginWindow.DeleteBrowserProfile();
        ReturnToLogin("ログアウトしました。");
    }

    /// <summary>メイン画面を畳んでログイン画面に戻す。セッション切れなどでも使う。</summary>
    private void ReturnToLogin(string message)
    {
        _user = null;
        _allItems.Clear();
        AvatarList.ItemsSource = null;
        MainPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        SetLoginStatus(message, error: false);
    }

    /// <summary>API が 401 を返した = セッションが無効。ログイン画面に戻して理由を伝える。true なら処理済み。</summary>
    private bool HandleSessionExpired(Exception ex)
    {
        if (ex is not VRChatApiException { IsUnauthorized: true }) return false;
        _ = _api.LogoutAsync();
        ReturnToLogin("VRChat のセッションが切れました。もう一度ログインしてください。");
        return true;
    }

    // ---------------- メイン ----------------

    private async Task EnterMainAsync(CurrentUser user)
    {
        _user = user;
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        UpdateUserHeader();
        if (!_osc.IsListening) StartOsc();
        await LoadAvatarsAsync();
    }

    private void UpdateUserHeader()
    {
        RefreshCurrentMarks();
        if (_user is null) return;
        UserNameText.Text = _user.DisplayName;
        var current = _allItems.FirstOrDefault(a => a.Id == _user.CurrentAvatar)?.Avatar;
        if (current is null) _avatarInfoCache.TryGetValue(_user.CurrentAvatar, out current);
        CurrentAvatarText.Text = current is not null ? $"現在: {current.Name}" : $"現在: {_user.CurrentAvatar}";
        CurrentAvatarText.ToolTip = current is not null ? $"{current.Name}\n{current.AuthorName}\n{current.Id}" : _user.CurrentAvatar;
        var thumb = current?.ThumbnailImageUrl ?? current?.ImageUrl;
        if (thumb is null && current is null) thumb = _user.CurrentAvatarThumbnailImageUrl;
        _ = LoadHeaderImageAsync(thumb);
        if (current is null && VRChatApi.IsValidAvatarId(_user.CurrentAvatar)) _ = ResolveCurrentAvatarAsync(_user.CurrentAvatar);
    }

    private readonly HashSet<string> _resolving = [];

    /// <summary>今の一覧に無い現在アバターの名前・サムネを API から 1 回だけ引く。</summary>
    private async Task ResolveCurrentAvatarAsync(string avatarId)
    {
        if (_avatarInfoCache.ContainsKey(avatarId) || !_resolving.Add(avatarId)) return;
        try
        {
            var av = await _api.GetAvatarAsync(avatarId);
            _avatarInfoCache[avatarId] = av;
            if (_user?.CurrentAvatar == avatarId) UpdateUserHeader();
        }
        catch { /* 取れなければ ID 表示のまま */ }
        finally { _resolving.Remove(avatarId); }
    }

    private async Task LoadHeaderImageAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) { CurrentAvatarImage.Source = null; return; }
        var img = await GetImageAsync(url, CancellationToken.None);
        if (_user is not null) CurrentAvatarImage.Source = img;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAvatarsAsync(refreshPublic: true);

    // ---------------- ウィンドウ位置の記憶 ----------------

    private void RestoreWindowBounds()
    {
        var s = _settings;
        if (s.WindowWidth is > 200 && s.WindowHeight is > 200 && s.WindowLeft is not null && s.WindowTop is not null)
        {
            // 画面外(モニタ構成が変わった等)に復元しない
            var vx = SystemParameters.VirtualScreenLeft; var vy = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth; var vh = SystemParameters.VirtualScreenHeight;
            if (s.WindowLeft + 100 < vx + vw && s.WindowTop + 100 < vy + vh && s.WindowLeft + s.WindowWidth > vx + 100 && s.WindowTop > vy - 50)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft.Value; Top = s.WindowTop.Value;
                Width = Math.Max(MinWidth, s.WindowWidth.Value); Height = Math.Max(MinHeight, s.WindowHeight.Value);
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        if (_preview) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = b.Left; _settings.WindowTop = b.Top;
        _settings.WindowWidth = b.Width; _settings.WindowHeight = b.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    // ---------------- 自動アップデート ----------------

    private UpdateInfo? _update;

    /// <summary>起動時に一度だけ最新リリースを確認する。見つかったらツールバーにボタンを出すだけで、勝手には更新しない。</summary>
    private async Task CheckForUpdateAsync()
    {
        _update = await Updater.CheckAsync();
        if (_update is null) return;
        UpdateButtonText.Text = $"v{_update.Version.ToString(3)} に更新";
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        // リリースに自動更新用の zip が添付されていない場合は、通知だけして何もしない
        if (_update.ZipUrl is null)
        {
            SetStatus(StatusKind.Info, $"v{_update.Version.ToString(3)} が公開されていますが、自動更新用のファイルがまだ添付されていません");
            return;
        }
        var notes = string.IsNullOrWhiteSpace(_update.Notes) ? "" : "\n\n" + _update.Notes.Trim();
        if (notes.Length > 400) notes = notes[..400] + "…";
        var ok = MessageBox.Show(this,
            $"バージョン {_update.Version.ToString(3)} に更新しますか?\n更新後、アプリは自動で再起動します。{notes}",
            "VRCAvatarChanger の更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        UpdateButton.IsEnabled = false;
        UpdateButtonText.Text = "更新しています";
        SetStatus(StatusKind.Info, "新しいバージョンをダウンロードしています");
        try
        {
            await Updater.DownloadAndApplyAsync(_update); // 成功したら再起動して戻らない
        }
        catch (Exception ex)
        {
            UpdateButton.IsEnabled = true;
            UpdateButtonText.Text = $"v{_update.Version.ToString(3)} に更新";
            SetStatus(StatusKind.Error, "更新できませんでした: " + FriendlyError.Of(ex));
        }
    }

    private HelpWindow? _help;

    private void HelpButton_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp(string tab = "howto")
    {
        if (_help is { IsLoaded: true }) { _help.Activate(); return; }
        _help = new HelpWindow(tab) { Owner = this };
        _help.Closed += (_, _) => _help = null;
        _help.Show();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1) { e.Handled = true; ShowHelp(); return; }
        if (e.Key == Key.Escape && _openGroup is not null && MainPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseGroup();
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && MainPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            ToggleStripeColors();
            return;
        }
        if (e.Key == Key.F5 && MainPanel.Visibility == Visibility.Visible && RefreshButton.IsEnabled)
        {
            e.Handled = true;
            RefreshButton_Click(sender, e);
        }
    }

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        PublicAddPanel.Visibility = SourcePublic.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _filterValue = null; // タブを移ったらフィルタは解除
        _openGroup = null;
        GroupBar.Visibility = Visibility.Collapsed;
        if (IsLoaded && MainPanel.Visibility == Visibility.Visible) _ = LoadAvatarsAsync();
    }

    private bool IsPublicTab => SourcePublic.IsChecked == true;

    /// <param name="refreshPublic">パブリックタブで、キャッシュ済みのアバター情報を API から取り直すか(「再読み込み」時のみ)</param>
    private async Task LoadAvatarsAsync(bool refreshPublic = false)
    {
        RefreshButton.IsEnabled = false;
        var favorites = SourceFavorites.IsChecked == true;
        var pub = IsPublicTab;
        SetStatus(StatusKind.Info, pub ? "パブリックリストを読み込んでいます" : favorites ? "お気に入りを読み込んでいます" : "自分のアバターを読み込んでいます");
        ShowListState(loading: true);
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;
        try
        {
            _allItems.Clear();
            if (pub)
            {
                if (refreshPublic) await RefreshPublicEntriesAsync(ct);
                _allItems.AddRange(_public.Entries.Select(e => new AvatarItem(e.Avatar) { AddedAt = e.AddedAt, Tags = _tags.TagsOf(e.Avatar.Id) }));
            }
            else
            {
                var avatars = favorites ? await _api.GetFavoriteAvatarsAsync(ct) : await _api.GetOwnAvatarsAsync(ct);
                // 自分のアバターにはタグを載せる（お気に入りはグループ表示を優先）
                _allItems.AddRange(avatars.Select(a => new AvatarItem(a) { Tags = favorites ? [] : _tags.TagsOf(a.Id) }));
            }
            ShowListState(loading: false);
            BuildFilterChips();
            ApplyFilter();
            UpdateUserHeader();
            SetStatus(StatusKind.Info, $"{_allItems.Count} 件");
            PruneImageCache();
            _ = LoadThumbnailsAsync(_allItems.ToList(), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowListState(loading: false);
            UpdateEmptyState(0);
            if (HandleSessionExpired(ex)) return;
            SetStatus(StatusKind.Error, "読み込めませんでした: " + FriendlyError.Of(ex));
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async Task LoadThumbnailsAsync(List<AvatarItem> items, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Where(i => i.ThumbnailUrl is not null).Select(async item =>
        {
            await gate.WaitAsync(ct);
            try { item.Thumbnail = await GetImageAsync(item.ThumbnailUrl!, ct); }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 今の一覧から参照されなくなった画像をキャッシュから外す(再読み込みで URL が変わった古いサムネなど)。
    /// 長時間の使用でメモリが増え続けないようにするための整理で、タブ切り替え程度では消えない量を上限にしている。
    /// </summary>
    private void PruneImageCache()
    {
        const int maxEntries = 600; // 320px デコードで 1 枚 ~300KB。600 枚 ≒ 180MB を上限の目安に
        if (_imageCache.Count <= maxEntries) return;
        var keep = new HashSet<string>(_allItems.Select(i => i.ThumbnailUrl).OfType<string>());
        if (_user?.CurrentAvatarThumbnailImageUrl is { } header) keep.Add(header);
        foreach (var url in _imageCache.Keys.Where(u => !keep.Contains(u)).ToList())
            _imageCache.Remove(url);
    }

    // ダウンロード中の URL。同じサムネをヘッダと一覧が同時に要求したときに二重ダウンロードしないための台帳
    private readonly Dictionary<string, Task<BitmapImage?>> _imageLoads = [];

    private Task<BitmapImage?> GetImageAsync(string url, CancellationToken ct)
    {
        if (_imageCache.TryGetValue(url, out var cached)) return Task.FromResult<BitmapImage?>(cached);
        if (_imageLoads.TryGetValue(url, out var inFlight)) return inFlight;
        var task = DownloadAndDecodeAsync(url, ct);
        _imageLoads[url] = task;
        return task;
    }

    private async Task<BitmapImage?> DownloadAndDecodeAsync(string url, CancellationToken ct)
    {
        try
        {
            var bytes = await _api.DownloadImageAsync(url, ct); // 失敗・キャンセル時は null (例外は投げない)
            if (bytes is null) return null;
            try
            {
                var img = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.DecodePixelWidth = 320; // ボックス表示 3 列でも粗くならない幅
                    img.StreamSource = ms;
                    img.EndInit();
                }
                img.Freeze();
                _imageCache[url] = img;
                return img;
            }
            catch { return null; }
        }
        finally { _imageLoads.Remove(url); } // 失敗・キャンセル分を台帳に残さない(次の要求で再試行できる)
    }

    private System.Windows.Threading.DispatcherTimer? _searchTimer;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (MainPanel.Visibility != Visibility.Visible) return;
        // 1 打鍵(IME の変換中含む)ごとに全件を並べ直すと重いので、入力が 200ms 止まってから 1 回だけ絞り込む
        if (_searchTimer is null)
        {
            _searchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _searchTimer.Tick += (_, _) => { _searchTimer!.Stop(); ApplyFilter(); };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        IEnumerable<AvatarItem> items = ApplyChipFilter(_allItems);
        if (q.Length > 0)
            items = items.Where(a =>
                a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.AuthorName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        var selectedId = (AvatarList.SelectedItem as AvatarItem)?.Id;
        var selectedWasGroup = (AvatarList.SelectedItem as AvatarItem)?.IsGroup == true;
        var sorted = ApplySort(items, SortKey).ToList();

        List<AvatarItem> list;
        if (_openGroup is not null)
        {
            // グループを開いている: そのメンバーだけ
            var memberIds = _openGroup.AvatarIds.ToHashSet(StringComparer.Ordinal);
            list = sorted.Where(a => memberIds.Contains(a.Id)).ToList();
            GroupBarName.Text = _openGroup.Name;
            GroupBarCount.Text = $"{list.Count} 体";
        }
        else if (!GroupViewOn)
        {
            // グループ化 OFF: すべて個別に表示
            list = sorted;
        }
        else
        {
            // グループに属するアバターは 1 枚のグループタイルにまとめる(代表 = 並び順で先頭のメンバー)
            var membership = _groups.BuildMembershipIndex();
            var byGroup = new Dictionary<AvatarGroup, List<AvatarItem>>();
            list = [];
            foreach (var a in sorted)
            {
                if (!membership.TryGetValue(a.Id, out var g)) { list.Add(a); continue; }
                if (!byGroup.TryGetValue(g, out var members)) byGroup[g] = members = [];
                members.Add(a);
            }
            list.AddRange(byGroup.Select(kv => new AvatarItem(kv.Key, kv.Value)));
            list = ApplySort(list, SortKey).ToList();
        }
        ApplyStripes(list);
        AvatarList.ItemsSource = list;
        var reselect = list.FirstOrDefault(a => a.Id == selectedId && a.IsGroup == selectedWasGroup);
        if (reselect is not null) AvatarList.SelectedItem = reselect;
        RefreshCurrentMarks();
        if (SkeletonPanel.Visibility != Visibility.Visible) UpdateEmptyState(list.Count);
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

    /// <summary>「現在着ているアバター」のチェックバッジを付け直す。</summary>
    private void RefreshCurrentMarks()
    {
        var cur = _user?.CurrentAvatar;
        if (AvatarList.ItemsSource is not IEnumerable<AvatarItem> items) return;
        foreach (var item in items)
            item.IsCurrent = item.IsGroup ? item.Members.Any(m => m.Id == cur) : item.Id == cur;
    }

    private void AvatarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ChangeButton.IsEnabled = AvatarList.SelectedItem is AvatarItem { IsAvatar: true };

    // ---------------- 着替え ----------------

    private async void ChangeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) await ChangeAvatarAsync(item.Id, item.Name);
        else SetStatus(StatusKind.Info, "一覧からアバターを選んでください");
    }

    private async void AvatarList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // タイル以外(余白)のダブルクリックは無視
        if (ItemAt(AvatarList, e.OriginalSource as DependencyObject) is not { } item) return;
        if (item.IsGroup) OpenGroup(item.Group!);
        else await ChangeAvatarAsync(item.Id, item.Name);
    }

    private async Task ChangeAvatarAsync(string avatarId, string name)
    {
        ChangeButton.IsEnabled = false;
        SetStatus(StatusKind.Info, $"{name} に着替えています");
        try
        {
            _user = await _api.SelectAvatarAsync(avatarId);
            UpdateUserHeader();
            SetStatus(StatusKind.Success, $"{name} に着替えました");
        }
        catch (Exception ex) { if (!HandleSessionExpired(ex)) SetStatus(StatusKind.Error, "着替えられませんでした: " + FriendlyError.Of(ex)); }
        finally { ChangeButton.IsEnabled = AvatarList.SelectedItem is AvatarItem { IsAvatar: true }; }
    }
}
