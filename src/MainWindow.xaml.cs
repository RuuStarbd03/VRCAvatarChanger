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
        SortBox.SelectedItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == savedSort) ?? SortBox.Items[0];
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
        Closing += (_, _) => SaveWindowBounds();
        Closed += (_, _) => { _settingsSaveTimer?.Stop(); _searchTimer?.Stop(); _oscRetry?.Stop(); _thumbCts?.Cancel(); _osc.Dispose(); _api.Dispose(); };
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

    internal static IEnumerable<AvatarItem> ApplySort(IEnumerable<AvatarItem> items, string key)
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
        AccountDesc.Text = (string.IsNullOrEmpty(_user?.DisplayName) ? "" : $"{_user.DisplayName} としてログイン中。")
            + "保存したログイン状態を消してログイン画面に戻ります。";
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
