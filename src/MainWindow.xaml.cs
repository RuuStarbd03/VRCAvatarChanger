using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

// メインウィンドウ本体: 状態の一覧・初期化と、一覧の読み込み / 並び順 / 絞り込み / 着替え。
// 機能ごとの処理は partial で分割している:
//   MainWindow.Auth.cs    ログイン / 2FA / セッション
//   MainWindow.Groups.cs  グループ操作 + ドラッグ&ドロップ
//   MainWindow.Public.cs  パブリックリスト
//   MainWindow.Filters.cs フィルタチップ / タグ編集 / 色分け
//   MainWindow.Images.cs  サムネイル取得・キャッシュ
//   MainWindow.Osc.cs     OSC 連携
//   MainWindow.Updates.cs 自動アップデート
//   MainWindow.Preview.cs UI プレビュー / 自己診断 (Debug のみ)
public partial class MainWindow : Window
{
    public static readonly DependencyProperty GridColumnsProperty =
        DependencyProperty.Register(nameof(GridColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(5));

    /// <summary>ボックス表示のときの 1 行あたりの数。ItemsPanel の VirtualizingUniformGrid が参照する。</summary>
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
#if DEBUG
        // UI 確認用: 環境変数 VRCAC_UI_PREVIEW=1 で API を叩かずにダミーデータでメイン画面を表示する (Debug ビルドのみ)。
        // スタートアップ登録やキーボードフックなど実環境に触る初期化より先に判定する
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW") == "1")
        {
            _preview = true;
            Loaded += (_, _) => ShowUiPreview();
        }
#endif
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
        InitSortControls(savedSort);
        InitWatchVRChat(); // _ready 前に呼ぶ (トグルの初期化でイベントを発火させない)
        InitQuickOverlay();
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
        Loaded += async (_, _) => { if (!_preview) await TryRestoreSessionAsync(); };
        Loaded += async (_, _) => { if (!_preview) await CheckForUpdateAsync(); };
        // 溜まりすぎたサムネイルのディスクキャッシュを起動時に 1 回だけ整理する (UI は待たせない)
        Loaded += (_, _) => { if (!_preview) _ = Task.Run(ImageDiskCache.Trim); };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            PumpThumbnails();  // トレイから開き直したら、止めておいたサムネイルの読み込みを再開する
            ResumeWarming();
            // 開きっぱなし / トレイ常駐で時間が経っていることがあるので、古ければ取り直す
            // (5 分以内ならキャッシュを使い、中身が同じなら一覧も作り直さないので、開くたびの負担にはならない)
            if (!_preview && _user is not null && MainPanel.Visibility == Visibility.Visible) _ = LoadAvatarsAsync();
        };
        Closing += (_, _) => SaveWindowBounds();
        Closed += (_, _) => { _settingsSaveTimer?.Stop(); _searchTimer?.Stop(); _oscRetry?.Stop(); _updateTimer?.Stop(); _thumbCts?.Cancel(); _osc.Dispose(); _api.Dispose(); };
    }

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
        // 表示形式が変わるとサムネに要る幅も変わる。大きい版に差し替わるのは画面に出たものだけで、
        // 残りはスクロールして見えたときに差し替わる (見てもいない数百枚を展開しない)
        QueueThumbnails(_allItems.ToList(), _thumbCts?.Token ?? CancellationToken.None);
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

    /// <summary>
    /// 保存・並び替えに使うキー ("created_desc" など)。画面では「何で並べるか」と
    /// 「昇順 / 降順」を別の操作に分けているので、ここで元の形に組み直す。
    /// </summary>
    private string SortKey
        => ((SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "created")
           + (SortDescToggle.IsChecked == true ? "_desc" : "_asc");

    /// <summary>保存されたキーを、種別のコンボと向きのトグルに振り分ける。</summary>
    private void InitSortControls(string savedKey)
    {
        var desc = savedKey.EndsWith("_desc", StringComparison.Ordinal);
        var kind = savedKey.Replace("_desc", "").Replace("_asc", "");
        SortBox.SelectedItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == kind)
                               ?? SortBox.Items[0];
        SortDescToggle.IsChecked = desc;
        UpdateSortDirection();
    }

    /// <summary>向きの見た目 (矢印とツールチップ) を今の状態に合わせる。</summary>
    private void UpdateSortDirection()
    {
        var desc = SortDescToggle.IsChecked == true;
        SortDirIcon.Text = desc ? "↓" : "↑";
        // 向きの意味は種別で変わるので、言い方もそれに合わせる
        SortDescToggle.ToolTip = (string?)(SortBox.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "name" => desc ? "名前の降順 (Z → A)" : "名前の昇順 (A → Z)",
            "author" => desc ? "作者名の降順 (Z → A)" : "作者名の昇順 (A → Z)",
            "recent" => desc ? "使っていないものから" : "最近使ったものから",
            "performance" => desc ? "重い順" : "軽い順",
            _ => desc ? "新しい順" : "古い順",
        };
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateSortDirection();
        SaveSortKey();
    }

    private void SortDesc_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateSortDirection();
        SaveSortKey();
    }

    private void SaveSortKey()
    {
        _settings.SortKey = SortKey;
        if (!_preview) _settings.Save();
        ApplyFilter();
    }

    /// <summary>最近使用に記録する (先頭 = 最新)。クイック着替えの「最近使用した順」に使う。</summary>
    private void TouchRecentAvatar(string avatarId)
    {
        if (!VRChatApi.IsValidAvatarId(avatarId)) return;
        var list = _settings.RecentAvatars;
        if (list.FirstOrDefault() == avatarId) return;
        list.Remove(avatarId);
        list.Insert(0, avatarId);
        if (list.Count > 200) list.RemoveRange(200, list.Count - 200);
        if (!_preview) _settings.Save();
    }

    /// <summary>
    /// 名前の下の 1 行を、今の並び順に合わせて入れ替える。
    /// 「その順で並べた根拠」がタイル上で見えるようにするため (日付順なら日付を出す)。
    /// 手がかりが無い並び (名前 / 作者 / 最近使った) は作者名のまま。
    /// </summary>
    private void ApplySubText(IEnumerable<AvatarItem> items, string key)
    {
        var kind = key.Replace("_desc", "").Replace("_asc", "");
        foreach (var a in items)
        {
            a.SubText2 = null; // 3 行目はパフォーマンス順のときだけ使う
            // グループは「N 体」を出したいので触らない
            if (a.IsGroup) { a.SubText = a.AuthorName; continue; }
            if (kind == "performance")
            {
                // サイズはまだ分からないことがある。取れたら行の中身だけ書き換わる
                ApplyPerformanceText(a);
                continue;
            }
            a.SubText = kind switch
            {
                "created" => Date(a.AddedAt ?? a.Avatar.CreatedAt) ?? a.AuthorName,
                "updated" => Date(a.Avatar.UpdatedAt) ?? a.AuthorName,
                _ => a.AuthorName,
            };
        }
        static string? Date(DateTimeOffset? d) => d?.ToLocalTime().ToString("yyyy/MM/dd");
    }

    /// <summary>パフォーマンスランクの表示名。VRChat の画面と同じ言い方にする。</summary>
    internal static string? PerformanceLabel(string? rating) => rating switch
    {
        "Excellent" => "Excellent",
        "Good" => "Good",
        "Medium" => "Medium",
        "Poor" => "Poor",
        "VeryPoor" => "Very Poor",
        _ => null, // 未判定 (None など) は作者名に任せる
    };

    /// <param name="recent">「最近使った順」に使う使用履歴 (先頭が最新)。要らない並びでは省略できる。</param>
    internal static IEnumerable<AvatarItem> ApplySort(IEnumerable<AvatarItem> items, string key,
        IReadOnlyList<string>? recent = null)
    {
        var desc = key.EndsWith("_desc", StringComparison.Ordinal);
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        // 「追加日」はパブリックタブではリストに追加した日時、それ以外はアバターの作成日時。日時が無いものは末尾に寄せる
        static DateTimeOffset? Added(AvatarItem a) => a.AddedAt ?? a.Avatar.CreatedAt;
        // 使用履歴の順位。使っていないものは末尾へ。グループは中で一番新しく使ったメンバーで代表する
        var rank = recent?.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);
        int Recent(AvatarItem a)
        {
            if (rank is null) return int.MaxValue;
            if (!a.IsGroup) return rank.TryGetValue(a.Id, out var r) ? r : int.MaxValue;
            var best = int.MaxValue;
            foreach (var m in a.Members)
                if (rank.TryGetValue(m.Id, out var r) && r < best) best = r;
            return best;
        }
        // パフォーマンスはグループでは一番重いメンバーに合わせる (中に重いものがあると結局重い)
        static int Perf(AvatarItem a)
            => a.IsGroup && a.Members.Count > 0
                ? a.Members.Max(m => m.Avatar.Performance?.Rank ?? 5)
                : a.Avatar.Performance?.Rank ?? 5;
        return key switch
        {
            "created_asc" or "created_desc" => desc
                ? items.OrderByDescending(a => Added(a) ?? DateTimeOffset.MinValue).ThenBy(a => a.Name, cmp)
                : items.OrderBy(a => Added(a) ?? DateTimeOffset.MaxValue).ThenBy(a => a.Name, cmp),
            "updated_asc" or "updated_desc" => desc
                ? items.OrderByDescending(a => a.Avatar.UpdatedAt ?? DateTimeOffset.MinValue).ThenBy(a => a.Name, cmp)
                : items.OrderBy(a => a.Avatar.UpdatedAt ?? DateTimeOffset.MaxValue).ThenBy(a => a.Name, cmp),
            // 使用履歴は「先頭が最新」なので、昇順 (0 が先) がそのまま「最近使った順」になる
            "recent_asc" or "recent_desc" => desc
                ? items.OrderByDescending(Recent).ThenBy(a => a.Name, cmp)
                : items.OrderBy(Recent).ThenBy(a => a.Name, cmp),
            // 昇順は軽い順 (Excellent が先)、降順は重い順
            "performance_asc" or "performance_desc" => desc
                ? items.OrderByDescending(Perf).ThenBy(a => a.Name, cmp)
                : items.OrderBy(Perf).ThenBy(a => a.Name, cmp),
            "author_asc" or "author_desc" => desc
                ? items.OrderByDescending(a => a.AuthorName, cmp).ThenBy(a => a.Name, cmp)
                : items.OrderBy(a => a.AuthorName, cmp).ThenBy(a => a.Name, cmp),
            "name_desc" => items.OrderByDescending(a => a.Name, cmp),
            _ => items.OrderBy(a => a.Name, cmp),
        };
    }

    // ---------------- 一覧の右クリックメニュー ----------------

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
        MenuAssignHotkey.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;
        BuildFavoriteMenu(item);

        // 隠し機能が有効なときだけ出す
        MenuStripeExclude.Visibility = _settings.StripeColors ? Visibility.Visible : Visibility.Collapsed;
        MenuStripeExclude.Header = IsStripeExcluded(item) ? "カウントに戻す" : "カウントから除外";
    }

    private async void MenuChange_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) await ChangeAvatarAsync(item.Id, item.Name);
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

    // 色は SetResourceReference で結び付ける。ブラシを直接入れてしまうと、
    // あとで配色を切り替えたときにそこだけ前の色のまま残る

    private void SetStatus(StatusKind kind, string text)
    {
        StatusText.Text = text;
        switch (kind)
        {
            case StatusKind.Success:
                StatusIcon.Text = "\uE73E"; // Segoe Fluent Icons: CheckMark
                StatusIcon.SetResourceReference(ForegroundProperty, "SuccessBrush");
                StatusText.SetResourceReference(ForegroundProperty, "TextBrush");
                StatusIcon.Visibility = Visibility.Visible;
                break;
            case StatusKind.Error:
                StatusIcon.Text = "\uEA39"; // Segoe Fluent Icons: ErrorBadge
                StatusIcon.SetResourceReference(ForegroundProperty, "DangerBrush");
                StatusText.SetResourceReference(ForegroundProperty, "DangerBrush");
                StatusIcon.Visibility = Visibility.Visible;
                break;
            default:
                StatusText.SetResourceReference(ForegroundProperty, "MutedTextBrush");
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

    // ---------------- ユーザーヘッダー (現在のアバター表示) ----------------

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
        var img = await GetImageAsync(url, ListThumbWidth, CancellationToken.None);
        if (_user is not null) CurrentAvatarImage.Source = img;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAvatarsAsync(refresh: true);

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

    // ---------------- 設定 (アプリ内オーバーレイ) ----------------

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private bool _settingsOpen;

    private void QuickToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SetQuickOverlay(QuickToggle.IsChecked == true);
    }

    private void KeepLoginToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var enabled = KeepLoginToggle.IsChecked == true;
        if (_settings.KeepBrowserLogin == enabled) return;
        _settings.KeepBrowserLogin = enabled;
        if (!_preview) _settings.Save();
        if (!enabled)
        {
            // オフにした時点で、残っていたブラウザデータも消す
            BrowserLoginWindow.DeleteBrowserProfile();
            SetStatus(StatusKind.Info, "ブラウザのログイン状態を消去しました。今後はログイン成功のたびに消去します");
        }
        else
        {
            SetStatus(StatusKind.Info, "ブラウザのログイン状態を保持: オン。次回のブラウザログインから状態が残ります");
        }
    }

    private void OpenSettings()
    {
        WatchToggle.IsChecked = _settings.WatchVRChat;
        QuickToggle.IsChecked = _settings.QuickOverlay;
        KeepLoginToggle.IsChecked = _settings.KeepBrowserLogin;
        (_settings.Theme switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        AccountDesc.Text = (string.IsNullOrEmpty(_user?.DisplayName) ? "" : $"{_user.DisplayName} としてログイン中。")
            + "ログイン状態を消して戻ります。";
        if (SettingsOverlay.Visibility != Visibility.Visible)
        {
            SettingsOverlay.Opacity = 0;
            SettingsCardScale.ScaleX = SettingsCardScale.ScaleY = 0.96;
            SettingsOverlay.Visibility = Visibility.Visible;
        }
        AnimateSettings(open: true);
    }

    private void CloseSettings()
    {
        if (SettingsOverlay.Visibility != Visibility.Visible) return;
        AnimateSettings(open: false);
    }

    /// <summary>設定オーバーレイのフェード + カードの拡縮。閉じ切ったら Collapsed にする。</summary>
    private void AnimateSettings(bool open)
    {
        _settingsOpen = open;
        var dur = TimeSpan.FromMilliseconds(open ? 180 : 130);
        var fade = new DoubleAnimation(open ? 1 : 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (!open) fade.Completed += (_, _) => { if (!_settingsOpen) SettingsOverlay.Visibility = Visibility.Collapsed; };
        SettingsOverlay.BeginAnimation(OpacityProperty, fade);

        var scale = new DoubleAnimation(open ? 1 : 0.96, dur)
        {
            EasingFunction = open
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        SettingsCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        SettingsCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void SettingsBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseSettings();

    private void WatchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SetWatchVRChat(WatchToggle.IsChecked == true);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var mode = (sender as RadioButton)?.Tag as string ?? "system";
        if (_settings.Theme == mode) return; // 設定画面を開いたときの初期化では何もしない
        _settings.Theme = mode;
        if (!_preview) _settings.Save();
        App.ApplyTheme(mode);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void SettingsLogout_Click(object sender, RoutedEventArgs e)
    {
        CloseSettings();
        Logout();
    }

    // ---------------- ヘルプ / キー操作 ----------------

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
        if (e.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseSettings();
            return;
        }
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

    // ---------------- 一覧の読み込み ----------------

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

    /// <summary>この時間内に取った一覧は取り直さない(タブの行き来だけで毎回 API を叩かないため)。</summary>
    private static readonly TimeSpan ListCacheFreshFor = TimeSpan.FromMinutes(5);

    /// <param name="refresh">「再読み込み」から呼ばれたか(パブリックのアバター情報とお気に入りの状態も取り直す)</param>
    private async Task LoadAvatarsAsync(bool refresh = false)
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
            int? refreshedEntries = null;
            if (pub)
            {
                if (refresh) refreshedEntries = await RefreshPublicEntriesAsync(ct);
                _allItems.Clear();
                _allItems.AddRange(_public.Entries.Select(e => new AvatarItem(e.Avatar) { AddedAt = e.AddedAt, Tags = _tags.TagsOf(e.Avatar.Id) }));
            }
            else
            {
                var kind = favorites ? AvatarListCache.Favorites : AvatarListCache.Own;
                var cached = _user is null ? null : AvatarListCache.Load(kind, _user.Id);
                // つい先ほど取ったばかりなら、そのまま使う (タブを行き来するたびに取り直さない)。
                // 「再読み込み」は常に取りに行くので、新しくアップロードしたアバターもすぐ出せる
                if (!refresh && cached is not null && DateTimeOffset.Now - cached.FetchedAt < ListCacheFreshFor)
                {
                    var note = $" ({cached.FetchedAt:HH:mm} 時点・F5 で取り直し)";
                    // すでに同じものを出しているなら作り直さない (トレイから開き直したときなど)
                    if (AlreadyShowing(cached.Avatars))
                    {
                        ShowListState(loading: false);
                        SetStatus(StatusKind.Info, CountText() + note);
                    }
                    else ShowAvatars(cached.Avatars, favorites, ct, note);
                    return;
                }
                // 前回の一覧があれば先に見せる。サムネもディスクキャッシュから戻るので待たされない
                if (cached is not null)
                {
                    if (AlreadyShowing(cached.Avatars)) ShowListState(loading: false);
                    else ShowAvatars(cached.Avatars, favorites, ct, " (前回の一覧・最新を確認しています)");
                }
                try
                {
                    var avatars = favorites ? await _api.GetFavoriteAvatarsAsync(ct) : await _api.GetOwnAvatarsAsync(ct);
                    if (_user is not null) AvatarListCache.Save(kind, _user.Id, avatars);
                    if (AlreadyShowing(avatars))
                    {
                        // 取り直したが中身は同じだった。一覧を作り直さない
                        // (件数によらず作り直しだけで 20ms 前後かかり、スクロール位置も先頭に戻ってしまう)
                        ShowListState(loading: false);
                        SetStatus(StatusKind.Info, CountText());
                        if (refresh) _ = RefreshFavoriteStateAsync(ct);
                        return;
                    }
                    _allItems.Clear();
                    // 自分のアバターにはタグを載せる（お気に入りはグループ表示を優先）
                    _allItems.AddRange(avatars.Select(a => new AvatarItem(a) { Tags = favorites ? [] : _tags.TagsOf(a.Id) }));
                }
                catch (Exception ex) when (cached is not null
                                           && ex is not OperationCanceledException
                                           && ex is not VRChatApiException { IsUnauthorized: true })
                {
                    // 出せる一覧はもう出してある。最新に追いつけなかったことだけ伝える
                    SetStatus(StatusKind.Error,
                        $"最新の一覧を取得できませんでした ({FriendlyError.Of(ex)}) {cached.FetchedAt:M/d HH:mm} 時点の一覧を表示しています。");
                    return;
                }
            }
            ShowListState(loading: false);
            BuildFilterChips();
            ApplyFilter();
            UpdateUserHeader();
            SetStatus(StatusKind.Info, CountText() + refreshedEntries switch
            {
                null => "",
                0 => " (情報は最新です)",
                var n => $" ({n} 件の情報を更新しました)",
            });
            QueueThumbnails(_allItems.ToList(), ct);
            if (refresh) _ = RefreshFavoriteStateAsync(ct);
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

    /// <summary>アバター列を一覧に載せて表示する(取得したものでも、キャッシュから読んだものでも同じ扱い)。</summary>
    private void ShowAvatars(List<Avatar> avatars, bool favorites, CancellationToken ct, string note)
    {
        _allItems.Clear();
        _allItems.AddRange(avatars.Select(a => new AvatarItem(a) { Tags = favorites ? [] : _tags.TagsOf(a.Id) }));
        ShowListState(loading: false);
        BuildFilterChips();
        ApplyFilter();
        UpdateUserHeader();
        SetStatus(StatusKind.Info, CountText() + note);
        QueueThumbnails(_allItems.ToList(), ct);
    }

    /// <summary>今まさに同じ内容を表示中か (何も出していないときは false)。</summary>
    private bool AlreadyShowing(List<Avatar> avatars)
        => _allItems.Count > 0 && SameAvatars(_allItems, avatars);

    /// <summary>今出ている一覧と、取り直した結果が同じ内容か (表示に関わる項目だけを見る)。</summary>
    private static bool SameAvatars(List<AvatarItem> shown, List<Avatar> fetched)
    {
        if (shown.Count != fetched.Count) return false;
        for (var i = 0; i < shown.Count; i++)
        {
            var a = shown[i].Avatar;
            var b = fetched[i];
            if (a.Id != b.Id || a.Name != b.Name || a.AuthorName != b.AuthorName
                || a.ThumbnailImageUrl != b.ThumbnailImageUrl || a.ImageUrl != b.ImageUrl
                || a.ReleaseStatus != b.ReleaseStatus || a.FavoriteGroup != b.FavoriteGroup
                || a.CreatedAt != b.CreatedAt || a.UpdatedAt != b.UpdatedAt) return false;
        }
        return true;
    }

    /// <summary>
    /// ステータスに出す件数。グループ化でタイルにまとまっているぶんは一覧に個別に並ばないので、
    /// 「件数は合っているのに 1 体見当たらない」と思わせないよう、まとめた数も添える。
    /// </summary>
    private string CountText()
    {
        var tiles = AvatarList.Items.OfType<AvatarItem>().ToList();
        var groups = tiles.Count(i => i.IsGroup);
        var folded = tiles.Where(i => i.IsGroup).Sum(i => i.Count) - groups;
        return folded > 0
            ? $"{_allItems.Count} 件 (うち {folded} 体は {groups} 個のグループにまとめて表示)"
            : $"{_allItems.Count} 件";
    }

    // ---------------- 検索と絞り込みの適用 ----------------

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
        var sorted = ApplySort(items, SortKey, _settings.RecentAvatars).ToList();

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
            list = ApplySort(list, SortKey, _settings.RecentAvatars).ToList();
        }
        // 並びも中身も今と同じなら入れ直さない。一覧の作り直しは件数によらず 20ms 前後かかるうえ、
        // スクロール位置と選択が先頭に戻ってしまう (タグ編集や色分けの切り替えなど、見た目が変わらない場合に効く)
        if (AvatarList.ItemsSource is List<AvatarItem> shown && SameContent(shown, list)) list = shown;
        ApplySubText(list, SortKey);
        ApplyStripes(list);
        if (!ReferenceEquals(AvatarList.ItemsSource, list)) AvatarList.ItemsSource = list;
        var reselect = list.FirstOrDefault(a => a.Id == selectedId && a.IsGroup == selectedWasGroup);
        if (reselect is not null) AvatarList.SelectedItem = reselect;
        RefreshCurrentMarks();
        if (SkeletonPanel.Visibility != Visibility.Visible) UpdateEmptyState(list.Count);
    }

    /// <summary>
    /// 表示中の並びと中身が同じか。同じアバター (同じインスタンス) が同じ順に並んでいて、
    /// グループタイルも同じグループ・同じメンバーを指していれば「同じ」とみなす。
    /// インスタンスまで見るのは、作り直した項目に差し替え損ねるとサムネイルの反映先がずれるため。
    /// </summary>
    private static bool SameContent(List<AvatarItem> shown, List<AvatarItem> list)
    {
        if (shown.Count != list.Count) return false;
        for (var i = 0; i < list.Count; i++)
        {
            var a = shown[i];
            var b = list[i];
            if (ReferenceEquals(a, b)) continue;
            // グループタイルは絞り込みのたびに作り直されるので、指している中身で見る
            if (!a.IsGroup || !b.IsGroup || !ReferenceEquals(a.Group, b.Group)
                || !a.Members.SequenceEqual(b.Members)) return false;
        }
        return true;
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

    /// <summary>着替える。成功したら true (クイック着替えオーバーレイが結果表示に使う)。</summary>
    private async Task<bool> ChangeAvatarAsync(string avatarId, string name)
    {
        ChangeButton.IsEnabled = false;
        SetStatus(StatusKind.Info, $"{name} に着替えています");
        try
        {
            // VRChat が OSC で繋がっていればローカルで即切替 (ヘッダーはエコー受信側が更新する)。
            // ダメなら従来どおり API で切り替える (ゲーム未起動時は次回起動時のアバターとして予約される)
            if (!await TryOscChangeAsync(avatarId))
            {
                _user = await _api.SelectAvatarAsync(avatarId);
                UpdateUserHeader();
            }
            SetStatus(StatusKind.Success, $"{name} に着替えました");
            TouchRecentAvatar(avatarId);
            return true;
        }
        catch (Exception ex)
        {
            if (!HandleSessionExpired(ex)) SetStatus(StatusKind.Error, "着替えられませんでした: " + FriendlyError.Of(ex));
            return false;
        }
        finally { ChangeButton.IsEnabled = AvatarList.SelectedItem is AvatarItem { IsAvatar: true }; }
    }
}
