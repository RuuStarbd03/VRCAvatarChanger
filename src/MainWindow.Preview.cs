#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;

namespace VRCAvatarChanger;

// UI 確認用のプレビューモードと自己診断 (Debug ビルド限定)。
// 環境変数 VRCAC_UI_PREVIEW=1 で API を叩かずにダミーデータでメイン画面を表示する。
public partial class MainWindow
{
    /// <summary>ダミーデータに順番に振るパフォーマンスランク。</summary>
    private static readonly string[] PerfRatings = ["Excellent", "Good", "Medium", "Poor", "VeryPoor"];

    private void ShowUiPreview()
    {
        // 見た目の確認は両テーマで行いたいので、設定を書き換えずにここだけ切り替えられるようにする
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_THEME") is { Length: > 0 } theme) App.ApplyTheme(theme);
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_ACCENT") is { Length: > 0 } accentKey) { _settings.Accent = accentKey; App.ApplyAccent(accentKey); }
        _user = new CurrentUser { DisplayName = "Preview User", CurrentAvatar = "avtr_00000000-0000-4000-8000-000000000001" };
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        // タブはダミーを入れる前に切り替える (切り替えると読み込みが走って一覧が入れ直されるため)
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SOURCE") == "public") SourcePublic.IsChecked = true;
        _allItems.Clear();
        string[] names = ["Kikyo", "Selestia", "Manuka", "Shinano", "Rurune", "Moe", "Lime", "Mizuki"];
        // VRCAC_UI_PREVIEW_COUNT=500 のように件数を増やして、仮想化やスクロールの負荷を確認できる
        var count = int.TryParse(Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_COUNT"), out var c) ? c : names.Length;
        // サムネイルの読み込みを試すとき用。ダミーの URL を付ける (事前にディスクキャッシュへ入れておく前提)
        var withThumbs = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_THUMBS") == "1";
        for (var i = 0; i < count; i++)
            _allItems.Add(new AvatarItem(new Avatar
            {
                Id = $"avtr_00000000-0000-4000-8000-{i + 1:D12}",
                ThumbnailImageUrl = withThumbs ? PreviewThumbUrl(i) : null,
                Name = names[i % names.Length] + (i >= names.Length ? $" {i + 1}" : "") + (i % 3 == 0 ? " (改変)" : ""),
                AuthorName = "preview_author_" + (char)('a' + i % 4),
                ReleaseStatus = i % 2 == 0 ? "private" : "public",
                CreatedAt = DateTimeOffset.Now.AddDays(-i * 3),
                UpdatedAt = DateTimeOffset.Now.AddDays(-i),
                Performance = new AvatarPerformance { Windows = PerfRatings[i % PerfRatings.Length] },
                UnityPackages =
                [
                    new UnityPackage
                    {
                        AssetUrl = $"https://api.vrchat.cloud/api/1/file/file_preview{i:D4}/1/file",
                        Platform = "standalonewindows",
                        Variant = "standard",
                    },
                ],
            }));
        // ダウンロードサイズの見た目確認用。プレビューでは取りに行かないので、ここで入れておく
        // (書き出すのは実際に取得したときだけなので、この値がファイルに残ることはない)
        for (var i = 0; i < count; i++)
            AssetSizeCache.Set($"file_preview{i:D4}", 1, 3_000_000L + i * 9_400_000L);
        // 「最近使った順」の確認用に、後ろのほうのアバターを使用済みにしておく
        _settings.RecentAvatars = Enumerable.Range(0, Math.Min(5, count))
            .Select(i => $"avtr_00000000-0000-4000-8000-{count - i:D12}").ToList();
        // 使えなくなったアバターの見た目確認: VRCAC_UI_PREVIEW_UNAVAILABLE=1 で 5 体に 1 体をそう扱う
        // (パブリックタブでの表示を見るためのもの。理由は削除 / 非公開を交互に)
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_UNAVAILABLE") == "1")
            for (var i = 0; i < _allItems.Count; i++)
                if (i % 5 == 2)
                {
                    _allItems[i].UnavailableSince = DateTimeOffset.Now.AddHours(-3 - i);
                    _allItems[i].UnavailableReason = i % 10 == 2 ? "deleted" : "private";
                    _allItems[i].IsUnavailable = true;
                }
        ShowListState(loading: Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_STATE") == "loading");
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_STATE") == "empty") _allItems.Clear();
        BuildFilterChips(); // 「使えなくなったもの」のチップなど、一覧の中身で決まるフィルタを出す
        ApplyFilter();
        if (withThumbs) QueueThumbnails(_allItems.ToList()); // 実際の読み込みと同じ経路を通す
        AvatarList.SelectedIndex = 1;
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_VIEW") is { Length: > 0 } view)
            (view == "grid" ? ViewGrid : ViewList).IsChecked = true;
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_GROUP") is { Length: > 0 } grp) GroupToggle.IsChecked = grp != "off";
        // 並び順の見た目確認: VRCAC_UI_PREVIEW_SORT="name_asc" のようにキーを渡す
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SORT") is { Length: > 0 } sort) InitSortControls(sort);
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
        SetStatus(StatusKind.Success, "アバターチェンジ完了: Kikyo");
        // 仮想化の自己診断: スクロールしながら実体化済みコンテナ数を VRCAC_UI_PREVIEW_REPORT のファイルに書いて終了する
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SCROLLTEST") == "1") _ = RunScrollTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_THUMBTEST") == "1") _ = RunThumbTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_PERFTEST") == "1") _ = RunPerfTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SOAKTEST") == "1") _ = RunSoakTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_GRIDTEST") == "1") _ = RunGridTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_GAPTEST") == "1") _ = RunGapTestAsync();
        // 自前で撮影まで行うので、下の共通の撮影経路 (先に終了してしまう) には進ませない
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_RETRYTEST") == "1") { _ = RunRetryTestAsync(); return; }
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SIZEPROBE") is { Length: > 0 } szPath) { _ = RunSizeProbeAsync(szPath); return; }
        // VR パネルの確認: VRCAC_UI_PREVIEW_VR=フォルダ で、パネルと手首ボタンの画像と、当たり判定・座標計算の結果を書き出して終了する
        // (SteamVR は使わないので、SteamVR の無い PC でも確かめられる)
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_VR") is { Length: > 0 } vrDir) { _ = RunVrTestAsync(vrDir); return; }
        // 誤操作でのグループ化対策の確認: VRCAC_UI_PREVIEW_GROUPSAFETY=フォルダ (重ねて待つと確定 / 元に戻す)
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_GROUPSAFETY") is { Length: > 0 } gsDir) { _ = RunGroupSafetyTestAsync(gsDir); return; }

        // 見た目確認: VRCAC_UI_PREVIEW_SHOT=path でウィンドウを画面外に置いたまま PNG に描画して終了する
        // (実画面をキャプチャしないので、ゲーム中でも邪魔にならない)。SETTINGS=1 なら設定オーバーレイを開いた状態で撮る
        // 位置計算の確認: VRCAC_UI_PREVIEW_PROBE=path で、クイック着替えの計算結果をファイルに書いて終了する
        // (画面には何も出さないので、VRChat のプレイ中でも邪魔にならない)
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_PROBE") is { Length: > 0 } probePath)
        {
            _ = WriteQuickProbeAsync(probePath);
            return;
        }

        var shotPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SHOT");

        // ログイン画面の確認: LOGIN=1 で入力画面を出す
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_LOGIN") is { Length: > 0 })
        {
            MainPanel.Visibility = Visibility.Collapsed;
            LoginPanel.Visibility = Visibility.Visible;
            CredentialsPanel.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(shotPath))
            {
                Left = -4000; Top = 0;
                _ = CaptureWindowAsync(this, shotPath);
            }
            return;
        }

        // 詳細画面の確認: VRCAC_UI_PREVIEW_DETAIL=n で n 番目 (表示中の並び) の詳細を開いた状態で撮る
        if (int.TryParse(Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_DETAIL"), out var detailAt))
        {
            var shown = AvatarList.Items.OfType<AvatarItem>().Where(a => a.IsAvatar).ToList();
            if (detailAt >= 0 && detailAt < shown.Count)
            {
                shown[detailAt].Avatar.Description = "プレビュー用の説明文です。\nPhysBone 対応 / 表情メニュー付き。\n改変・再配布は規約に従ってください。";
                OpenDetail(shown[detailAt]);
            }
        }
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SETTINGS") is { Length: > 0 } settingsAt)
        {
            OpenSettings();
            // "bottom" なら末尾まで送る (ログアウトなど下のほうの項目を撮るため)
            if (settingsAt == "bottom")
                Dispatcher.BeginInvoke(() => VisualTree.FindDescendant<ScrollViewer>(SettingsOverlay)?.ScrollToEnd(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
        }
        // 配色の切り替えの確認: 設定から選んだあと、その場で全体が変わっているかを撮る
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_THEMEPICK") is { Length: > 0 } pick)
        {
            Left = -4000; Top = 0;
            OpenSettings();
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                await Dispatcher.InvokeAsync(() =>
                    (pick switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true);
                if (!string.IsNullOrEmpty(shotPath))
                    await Dispatcher.InvokeAsync(() => CaptureWindowAsync(this, shotPath, 400));
            });
            return;
        }

        // スイッチの切り替えアニメーションの確認: 切り替えた直後 (滑っている最中) を撮る
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_TOGGLEANIM") is { Length: > 0 } toggleAt)
        {
            Left = -4000; Top = 0;
            _ = Task.Run(async () =>
            {
                await Task.Delay(700); // 設定が開き切るのを待つ
                await Dispatcher.InvokeAsync(() => QuickToggle.IsChecked = false);
                if (!string.IsNullOrEmpty(shotPath))
                    await Dispatcher.InvokeAsync(() => CaptureWindowAsync(this, shotPath, int.Parse(toggleAt)));
            });
            return;
        }
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_TOAST") == "1")
        {
            // ホットキーでの着替え結果を出す通知を画面外で撮る
            Left = -4000; Top = 0;
            var toast = new ToastWindow();
            toast.ShowMessage("アバターチェンジ完了: Selestia",
                new Win32.NativeRect { Left = -4400, Top = 0, Right = -4000, Bottom = 400 }, 1.0, error: false);
            if (!string.IsNullOrEmpty(shotPath)) _ = CaptureWindowAsync(toast, shotPath);
        }
        else if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_HELP") is { Length: > 0 } helpTab)
        {
            // 使い方の画面を画面外に開いて撮る (howto / faq / about)
            Left = -4000; Top = 0;
            var help = new HelpWindow
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4700,
                Top = 0,
            };
            help.Show();
            if (helpTab is "faq" or "faq-open") help.TabFaq.IsChecked = true;
            else if (helpTab == "about") help.TabAbout.IsChecked = true;
            // 開いた状態の確認: 先頭のいくつかを開いておく
            if (helpTab == "faq-open")
                foreach (var ex in help.FaqPanel.Children.OfType<Expander>().Take(3)) ex.IsExpanded = true;
            if (!string.IsNullOrEmpty(shotPath)) _ = CaptureWindowAsync(help, shotPath);
        }
        else if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_HOTKEYS") == "1")
        {
            // ホットキー割り当て画面を画面外に開いて撮る
            Left = -4000; Top = 0;
            var hotkeys = new HotkeyWindow(_settings, () => { })
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4400,
                Top = 0,
            };
            hotkeys.Show();
            if (!string.IsNullOrEmpty(shotPath)) _ = CaptureWindowAsync(hotkeys, shotPath);
        }
        else if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_QUICK") == "1")
        {
            // クイック着替えオーバーレイを画面外に開いて撮る
            Left = -4000; Top = 0;
            _quick = new QuickPickWindow(QuickChangeAsync, () => { }, SaveQuickSortKey, RequestThumbnail);
            _quick.OpenAt(new Win32.NativeRect { Left = -4400, Top = 0, Right = -4000, Bottom = 760 }, 1.0,
                FlatAvatarItems(), _settings.RecentAvatars,
                Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SORT") ?? _settings.QuickSortKey,
                Hotkey.Parse(_settings.QuickHotkey));
            if (!string.IsNullOrEmpty(shotPath)) _ = CaptureWindowAsync(_quick, shotPath);
        }
        else if (!string.IsNullOrEmpty(shotPath))
        {
            Left = -4000; Top = 0;
            _ = CaptureWindowAsync(this, shotPath);
        }
    }

    /// <summary>
    /// クイック着替えの位置計算を実際に走らせ、結果をテキストで書き出す。
    /// オーバーレイは画面外に開いて実測し、すぐ終了する。
    /// </summary>
    private async Task WriteQuickProbeAsync(string path)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            Left = -4000; Top = 0;
            var vrc = System.Diagnostics.Process.GetProcessesByName("VRChat").FirstOrDefault(p => p.MainWindowHandle != 0);
            _vrchatHwnd = vrc?.MainWindowHandle ?? 0;
            sb.AppendLine($"vrchat hwnd = {_vrchatHwnd}");
            sb.AppendLine($"client px   = {Fmt(Win32.ClientAreaPx(_vrchatHwnd))}");
            sb.AppendLine($"work px     = {Fmt(Win32.WorkAreaPx(_vrchatHwnd))}");
            var area = QuickOverlayAreaPx();
            var scale = Win32.ScaleOf(_vrchatHwnd);
            sb.AppendLine($"area px     = {Fmt(area)}");
            sb.AppendLine($"vrchat scale= {scale}");
            sb.AppendLine($"main scale  = {VisualTreeHelper.GetDpi(this).DpiScaleX}");

            _quick = new QuickPickWindow(QuickChangeAsync, () => { }, SaveQuickSortKey, RequestThumbnail);
            _quick.OpenAt(area, scale, FlatAvatarItems(), _settings.RecentAvatars, _settings.QuickSortKey,
                Hotkey.Parse(_settings.QuickHotkey));
            var wantW = (int)Math.Round(QuickPickWindow.DesignWidthDip * scale);
            sb.AppendLine($"want px     = x {area.Right - wantW} w {wantW} h {area.Bottom - area.Top}");

            foreach (var wait in new[] { 200, 800, 1500 })
            {
                await Task.Delay(wait);
                sb.AppendLine($"actual +{wait}ms = {Fmt(Win32.WindowRectPx(_quick.Hwnd))} " +
                              $"(wpf W {_quick.Width:F0} H {_quick.Height:F0}, dpi {VisualTreeHelper.GetDpi(_quick).DpiScaleX}) " +
                              $"slideX {_quick.Slide.X:F1} opacity {_quick.Root.Opacity:F2} visible {_quick.IsVisible} " +
                              $"active {_quick.IsActive} 手前={(Win32.GetForegroundWindow() == _quick.Hwnd ? "オーバーレイ" : Win32.GetForegroundWindow() == _vrchatHwnd ? "VRChat" : "その他")}");
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        File.WriteAllText(path, sb.ToString());
        Application.Current.Shutdown();
    }

    private static string Fmt(Win32.NativeRect? r)
        => r is { } v ? $"{v.Left},{v.Top} - {v.Right},{v.Bottom} ({v.Right - v.Left}x{v.Bottom - v.Top})" : "(null)";

    /// <summary>
    /// ダウンロードサイズが引けるかを確かめる (VRCAC_UI_PREVIEW_SIZEPROBE=path)。
    /// これだけは実際の API を叩く (保存されたセッションを使う)。サイズは 1 件 1 リクエスト
    /// なので、確認でも先頭 3 件に絞る。
    /// </summary>
    private async Task RunSizeProbeAsync(string path)
    {
        Left = -4000; Top = 0;
        var sb = new System.Text.StringBuilder();
        try
        {
            var me = await _api.TryGetCurrentUserAsync();
            sb.AppendLine($"ログイン: {me?.DisplayName ?? "できず"}");
            var avatars = await _api.GetOwnAvatarsAsync();
            sb.AppendLine($"一覧: {avatars.Count} 件");
            foreach (var a in avatars.Take(3))
            {
                var r = a.WindowsAssetRef;
                if (r is null) { sb.AppendLine($"{a.Name,-16} assetRef 取れず"); continue; }
                var size = await _api.GetAssetSizeAsync(r.Value.FileId, r.Value.Version);
                if (size is { } s) AssetSizeCache.Set(r.Value.FileId, r.Value.Version, s);
                var item = new AvatarItem(a);
                ApplyPerformanceText(item);
                sb.AppendLine($"{a.Name,-16} 生={(size is { } b ? $"{b:N0}" : "取れず"),-12} 表示=「{item.SubText}」+「{item.SubText2}」");
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex.Message); }
        File.WriteAllText(path, sb.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 取れなかったサムネイルが取り直されるかを見る (VRCAC_UI_PREVIEW_RETRYTEST=1)。
    /// 実際のキャッシュ (list-own.json) の URL を使うので、展開はディスクから完結する。
    /// VRCAC_TEST_FAIL_FIRST で最初の n 枚を失敗させ、時間を置いて埋まるかを測る。
    /// </summary>
    private async Task RunRetryTestAsync()
    {
        Left = -4000; Top = 0;
        Width = 1080; Height = 740;
        var reportPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_REPORT");
        var report = new System.Text.StringBuilder();
        try
        {
            var listPath = AppPaths.In(Path.Combine("cache", "list-own.json"));
            var real = JsonFile.Load<CachedAvatarList>(listPath, new System.Text.Json.JsonSerializerOptions());
            report.AppendLine($"実キャッシュ: {(real is null ? "読めず" : $"{real.Avatars.Count} 件")}");
            if (real is null) { File.WriteAllText(reportPath!, report.ToString()); Application.Current.Shutdown(); return; }

            _allItems.Clear();
            _allItems.AddRange(real.Avatars.Select(a => new AvatarItem(a)));
            ViewGrid.IsChecked = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_VIEW") != "list";
            var cols = int.TryParse(Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_COLUMNS"), out var pc) ? pc : 10;
            ColumnsSlider.Value = cols;
            GridColumns = cols;
            // GROUPLATE=1 のときは「グループ化オフで読み込み → あとからオンにする」を再現する
            var groupLate = Environment.GetEnvironmentVariable("VRCAC_TEST_GROUP_LATE") == "1";
            if (groupLate) GroupToggle.IsChecked = false;
            ApplyFilter();
            AvatarList.UpdateLayout();
            QueueThumbnails(_allItems.ToList());

            void Snap(string label)
            {
                // 添字は「表示中のリスト」のもの。_allItems (フラット) とは並びが違うので取り違えないこと
                var shown = AvatarList.Items.OfType<AvatarItem>().ToList();
                var visible = VisibleIndexes().Where(i => i < shown.Count).ToList();
                var blankIdx = visible.Where(i => shown[i].Thumbnail is null && shown[i].ThumbnailUrl is not null).ToList();
                var groups = visible.Where(i => shown[i].IsGroup).ToList();
                var groupBlank = groups.Where(i => shown[i].Thumbnail is null).ToList();
                report.AppendLine($"{label,-16} 一覧={shown.Count,3} 表示中={visible.Count,3} 画像なし={blankIdx.Count,3} " +
                    $"| グループタイル={groups.Count,2} うち画像なし={groupBlank.Count,2} " +
                    (groupBlank.Count > 0
                        ? "-> " + string.Join(",", groupBlank.Take(6).Select(i =>
                            $"{shown[i].Name}(代表:{shown[i].Representative?.Avatar.Name} 画像={(shown[i].Representative?.Thumbnail is null ? "なし" : "あり")} 幅={shown[i].Representative?.ThumbnailWidth})"))
                        : ""));
            }

            foreach (var wait in new[] { 1500, 2000, 3000 })
            {
                await Task.Delay(wait);
                AvatarList.UpdateLayout();
                Snap($"+{wait}ms");
            }
            if (groupLate)
            {
                GroupToggle.IsChecked = true; // ここで初めてグループタイルが作られる
                AvatarList.UpdateLayout();
                await Task.Delay(1500);
                Snap("グループ化オン");
            }
            // グループタイルは代表 (最古のメンバー) の順に並ぶので下の方にいる。そこまで送って見る
            var all = AvatarList.Items.OfType<AvatarItem>().ToList();
            var firstGroup = all.FindIndex(a => a.IsGroup);
            report.AppendLine($"グループタイルの位置: {string.Join(",", all.Select((a, i) => (a, i)).Where(t => t.a.IsGroup).Select(t => $"#{t.i}:{t.a.Name}"))}");
            if (firstGroup >= 0)
            {
                AvatarList.ScrollIntoView(all[firstGroup]);
                AvatarList.UpdateLayout();
                await Task.Delay(2000);
                Snap("グループ位置へ");
                await Task.Delay(3000);
                Snap("さらに待つ");
                // 一覧の読み込みをやり直したが中身が同じだった (F5 / トレイから開き直し) 状態にしてから、
                // グループを開く。中身はまだ画面に出ていないので、ここで初めて展開される
                _loadCts?.Cancel();
                _loadCts = new CancellationTokenSource();
                OpenGroup(all[firstGroup].Group!);
                AvatarList.UpdateLayout();
                await Task.Delay(2000);
                Snap("グループを開く");
            }
            if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SHOT") is { Length: > 0 } shot)
            {
                if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
                await CaptureWindowAsync(this, shot, 300); // 撮ったら終了する
                return;
            }
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 実際に配置されたタイルの項目番号を集め、連番に抜けが無いかを見る (VRCAC_UI_PREVIEW_GAPTEST=1)。
    /// 「一覧に居るのにタイルが出ない」が起きていないかの判定用。
    /// </summary>
    private async Task RunGapTestAsync()
    {
        Left = -4000; Top = 0;
        Width = 1080; Height = 740; // 利用者のウィンドウサイズに合わせる
        var reportPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_REPORT");
        var report = new System.Text.StringBuilder();
        try
        {
            var columns = int.TryParse(Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_COLUMNS"), out var c) ? c : 10;
            ViewGrid.IsChecked = true;
            ColumnsSlider.Value = columns;
            GridColumns = columns;
            AvatarList.UpdateLayout();
            await Task.Delay(1500);

            var items = AvatarList.Items.OfType<AvatarItem>().ToList();
            var panel = VisualTree.FindDescendant<VirtualizingUniformGrid>(AvatarList);
            report.AppendLine($"件数={items.Count} 列数={columns} panel={(panel is null ? "見つからず" : "あり")}");

            void Check(string label)
            {
                if (panel is null) return;
                var idx = new List<int>();
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
                {
                    if (VisualTreeHelper.GetChild(panel, i) is FrameworkElement { DataContext: AvatarItem it })
                    {
                        var at = items.IndexOf(it);
                        if (at >= 0) idx.Add(at);
                    }
                }
                idx.Sort();
                var gaps = new List<int>();
                for (var i = 1; i < idx.Count; i++)
                    for (var missing = idx[i - 1] + 1; missing < idx[i]; missing++) gaps.Add(missing);
                var dup = idx.Count - idx.Distinct().Count();
                var h = panel.HeightState;
                // タイル 1 枚が本当に要る高さ (中身を測り直した値)。行高がこれより大きいと縦長に見える
                var natural = 0.0;
                if (VisualTreeHelper.GetChildrenCount(panel) > 0
                    && VisualTreeHelper.GetChild(panel, 0) is UIElement first)
                {
                    first.Measure(new Size(h.ItemWidth, double.PositiveInfinity));
                    natural = first.DesiredSize.Height;
                }
                report.AppendLine($"{label,-14} 配置={idx.Count,4} 範囲={(idx.Count > 0 ? $"{idx[0]}..{idx[^1]}" : "-"),-10} " +
                    $"重複={dup,2} 抜け={gaps.Count,3} | タイル幅={h.ItemWidth,6:F1} 行高={h.Row,6:F1} " +
                    $"見積り={h.Estimate,6:F1} 実際に要る高さ={natural,6:F1} 余り={h.Row - natural,6:F1} " +
                    $"{(gaps.Count > 0 ? "-> " + string.Join(",", gaps.Take(20)) : "")}");
            }

            Check("初期表示");
            var sv = VisualTree.FindDescendant<ScrollViewer>(AvatarList);
            for (var i = 1; i <= 6; i++)
            {
                sv?.ScrollToVerticalOffset((sv.ExtentHeight - sv.ViewportHeight) * i / 6.0);
                AvatarList.UpdateLayout();
                await Task.Delay(500);
                Check($"スクロール {i}/6");
            }
            sv?.ScrollToVerticalOffset(0);
            AvatarList.UpdateLayout();
            await Task.Delay(500);
            Check("先頭へ戻す");
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>
    /// ボックス表示で一度にたくさん見えるとき (10 列・大きなウィンドウ) の実体化数と保持量を測る
    /// (VRCAC_UI_PREVIEW_GRIDTEST=1)。スクロールしても見えているものが空白にならないかも見る。
    /// </summary>
    private async Task RunGridTestAsync()
    {
        Left = -4000; Top = 0;
        Width = 1900; Height = 1250; // 大きめのモニターでの最大化に近い状態
        var reportPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_REPORT");
        var report = new System.Text.StringBuilder();
        try
        {
            var columns = int.TryParse(Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_COLUMNS"), out var c) ? c : 10;
            ViewGrid.IsChecked = true;
            ColumnsSlider.Value = columns;
            GridColumns = columns;
            AvatarList.UpdateLayout();
            await Task.Delay(1800);

            var items = AvatarList.Items.OfType<AvatarItem>().ToList();
            void Snapshot(string label)
            {
                var visible = VisibleIndexes().Where(i => i < items.Count).ToList();
                var blank = visible.Count(i => items[i].Thumbnail is null && items[i].ThumbnailUrl is not null);
                var live = items.Count(i => i.Thumbnail is not null);
                var bytes = items.Where(i => i.Thumbnail is not null)
                    .Sum(i => (long)i.Thumbnail!.PixelWidth * i.Thumbnail!.PixelHeight * 4);
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                report.AppendLine($"{label,-14} 実体化={visible.Count,4} 表示中の空白={blank,3} " +
                    $"保持画像={live,4}枚/{bytes / 1024 / 1024,3}MB ﾌﾟﾗｲﾍﾞｰﾄ={proc.PrivateMemorySize64 / 1024 / 1024,4}MB");
            }
            report.AppendLine($"ウィンドウ={Width}x{Height} 列数={GridColumns} 件数={items.Count} " +
                $"タイル幅={AvatarList.ActualWidth / GridColumns:F0}dip 展開幅={ThumbWidth}px");
            Snapshot("先頭");

            var sv = VisualTree.FindDescendant<ScrollViewer>(AvatarList);
            for (var i = 1; i <= 6; i++)
            {
                sv?.ScrollToVerticalOffset((sv.ExtentHeight - sv.ViewportHeight) * i / 6.0);
                AvatarList.UpdateLayout();
                await Task.Delay(700); // 展開が追いつく時間
                Snapshot($"スクロール {i}/6");
            }
            // 一気に先頭へ戻す (捨てたものを読み直せるか)
            sv?.ScrollToVerticalOffset(0);
            AvatarList.UpdateLayout();
            await Task.Delay(700);
            Snapshot("先頭へ戻す");
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 長時間開きっぱなしにしたときに何が増えるかを測る (VRCAC_UI_PREVIEW_SOAKTEST=1)。
    /// 検索・表示形式の切り替えを繰り返して、キャッシュ・メモリ・ハンドルの増え方を見る。
    /// </summary>
    private async Task RunSoakTestAsync()
    {
        Left = -4000; Top = 0;
        var reportPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_REPORT");
        var report = new System.Text.StringBuilder();
        try
        {
            await Task.Delay(1500); // 最初のサムネイル読み込みが一巡するまで

            void Snapshot(string label)
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                proc.Refresh();
                var live = _allItems.Count(i => i.Thumbnail is not null);
                var bytes = _allItems.Where(i => i.Thumbnail is not null)
                    .Sum(i => (long)i.Thumbnail!.PixelWidth * i.Thumbnail!.PixelHeight * 4);
                report.AppendLine(
                    $"{label,-16} 保持画像={live,4}枚/{bytes / 1024 / 1024,4}MB " +
                    $"管理ﾒﾓﾘ={GC.GetTotalMemory(false) / 1024 / 1024,4}MB " +
                    $"ﾌﾟﾗｲﾍﾞｰﾄ={proc.PrivateMemorySize64 / 1024 / 1024,4}MB 作業ｾｯﾄ={proc.WorkingSet64 / 1024 / 1024,4}MB " +
                    $"ﾊﾝﾄﾞﾙ={proc.HandleCount,5} GC2={GC.CollectionCount(2),3}");
            }

            Snapshot("開始 (リスト)");

            // ボックス表示の定常状態 (大きい版に差し替わる)
            ViewGrid.IsChecked = true;
            AvatarList.UpdateLayout();
            await Task.Delay(2500);
            Snapshot("ボックス表示");
            ViewList.IsChecked = true;
            AvatarList.UpdateLayout();
            await Task.Delay(1500);
            Snapshot("リストに戻す");

            // 検索の打鍵を 300 回ぶん
            for (var i = 0; i < 300; i++)
            {
                SearchBox.Text = (i % 3) switch { 0 => "改変", 1 => "Ki", _ => "" };
                ApplyFilter();
            }
            SearchBox.Text = "";
            ApplyFilter();
            AvatarList.UpdateLayout();
            Snapshot("検索 300 回後");

            // 表示形式の切り替えを 20 往復 (デコード幅が変わるので画像キャッシュが増えやすい)
            for (var i = 0; i < 20; i++)
            {
                ViewGrid.IsChecked = true;
                AvatarList.UpdateLayout();
                await Task.Delay(60);
                ViewList.IsChecked = true;
                AvatarList.UpdateLayout();
                await Task.Delay(60);
            }
            await Task.Delay(2000); // 裏の読み込みが落ち着くまで
            Snapshot("表示切替 20 往復後");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Snapshot("GC 後");
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 一覧の件数が多いときの処理時間を測る (VRCAC_UI_PREVIEW_PERFTEST=1)。
    /// 検索・並べ替え・グループ化のたびに走る処理が、何百体でも待たされない範囲に収まっているかの確認。
    /// </summary>
    private async Task RunPerfTestAsync()
    {
        Left = -4000; Top = 0;
        var reportPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_REPORT");
        var report = new System.Text.StringBuilder();
        try
        {
            await Task.Delay(800);

            // グループを作る (20 グループ × 5 体)。グループ化の負荷を実際の使い方に近づける
            var ids = _allItems.Select(a => a.Id).ToList();
            for (var g = 0; g + 5 <= Math.Min(ids.Count, 100); g += 5)
            {
                var group = _groups.Create($"グループ {g / 5}");
                for (var i = g; i < g + 5; i++) _groups.Assign(ids[i], group);
            }
            report.AppendLine($"items={_allItems.Count} groups={_groups.Groups.Count}");

            void Measure(string label, Action action, int n = 20)
            {
                action(); // ウォームアップ
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < n; i++) action();
                sw.Stop();
                report.AppendLine($"{label}: {sw.Elapsed.TotalMilliseconds / n:F2} ms/回");
            }

            GroupToggle.IsChecked = true;
            Measure("ApplyFilter (グループ化 ON)", () => ApplyFilter());
            Measure("ApplyFilter + レイアウト確定", () => { ApplyFilter(); AvatarList.UpdateLayout(); });
            GroupToggle.IsChecked = false;
            Measure("ApplyFilter (グループ化 OFF)", () => ApplyFilter());
            GroupToggle.IsChecked = true;
            Measure("BuildMembershipIndex", () => _groups.BuildMembershipIndex(), 200);
            SearchBox.Text = "改変";
            Measure("ApplyFilter (検索あり)", () => ApplyFilter());
            SearchBox.Text = "";
            Measure("並び替えのみ (ApplySort)", () => MainWindow.ApplySort(_allItems, "name_asc").ToList());

            // 中身が変わらない再絞り込み (タグ編集や色分けの切り替えなど) は作り直しを飛ばせているか
            Measure("ApplyFilter + レイアウト (中身が変わらない)", () => { ApplyFilter(); AvatarList.UpdateLayout(); });
            // 検索の 1 打鍵ぶん (毎回結果が変わるので作り直しは避けられない)
            var toggle = false;
            Measure("ApplyFilter + レイアウト (毎回内容が変わる)", () =>
            {
                SearchBox.Text = (toggle = !toggle) ? "改変" : "";
                ApplyFilter();
                AvatarList.UpdateLayout();
            });
            SearchBox.Text = "";

            // ボックス表示で列数スライダーを動かしている間の負荷
            ViewGrid.IsChecked = true;
            AvatarList.UpdateLayout();
            var cols = 5;
            Measure("列数変更 + レイアウト (ボックス表示)", () =>
            {
                cols = cols == 5 ? 6 : 5;
                GridColumns = cols;
                AvatarList.UpdateLayout();
            });
            ViewList.IsChecked = true;
            AvatarList.UpdateLayout();

            // どこが重いのかの切り分け: 同じ一覧を入れ直すだけ / テンプレートを簡素にした場合
            var current = AvatarList.ItemsSource;
            Measure("ItemsSource 入れ直しのみ + レイアウト", () =>
            {
                AvatarList.ItemsSource = null;
                AvatarList.ItemsSource = current;
                AvatarList.UpdateLayout();
            });
            var realTemplate = AvatarList.ItemTemplate;
            AvatarList.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "<TextBlock Text='{Binding Name}'/></DataTemplate>");
            Measure("ApplyFilter + レイアウト (簡易テンプレート)", () => { ApplyFilter(); AvatarList.UpdateLayout(); });
            AvatarList.ItemTemplate = realTemplate;

            // 一覧キャッシュの読み書き (起動のたびに通る)
            var avatars = _allItems.Select(a => a.Avatar).ToList();
            // 保存は UI スレッドで待つぶんだけ (書き出しは裏で行う)。読み込みは 2 回目以降はメモリから返る
            Measure("一覧キャッシュ 保存", () => AvatarListCache.Save(AvatarListCache.Own, "usr_perf", avatars), 10);
            Measure("一覧キャッシュ 読み込み", () => AvatarListCache.Load(AvatarListCache.Own, "usr_perf"), 10);
            await Task.Delay(500); // 裏の書き出しが終わるのを待ってから、ファイルからの読み込みを測る
            Measure("一覧キャッシュ 読み込み (起動直後・ファイルから)", () =>
            {
                AvatarListCache.DropMemory();
                AvatarListCache.Load(AvatarListCache.Own, "usr_perf");
            }, 10);
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>
    /// VR パネルの自己診断 (VRCAC_UI_PREVIEW_VR=フォルダ)。SteamVR には繋がず、次を確かめる:
    /// 描画 (PNG に書き出す)、押した場所の判定、行列の変換、手首ボタンの向き、触れた点から画像座標への変換。
    /// </summary>
    private async Task RunVrTestAsync(string dir)
    {
        Left = -4000; Top = 0;
        var report = new System.Text.StringBuilder();
        var failures = 0;
        void Expect(string label, bool ok, string detail = "")
        {
            if (!ok) failures++;
            report.AppendLine($"{(ok ? "OK  " : "NG  ")}{label}{(detail.Length > 0 ? "  " + detail : "")}");
        }
        void Save(BitmapSource bmp, string name)
        {
            using var stream = File.Create(Path.Combine(dir, name));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(stream);
        }
        try
        {
            Directory.CreateDirectory(dir);
            // openvr_api.dll が読めること (SteamVR が無ければ false が返るだけで、例外にならない)
            report.AppendLine($"OpenVR ランタイム: {(Valve.VR.OpenVR.IsRuntimeInstalled() ? "あり" : "なし")} (dll の読み込みは成功)");

            // ---- 準備: グループ 1 つ (3 体)、タグ 2 種類、お気に入りの一覧キャッシュ ----
            // (データはプレビュー用の一時フォルダにだけ書かれる)
            var ids = _allItems.Select(a => a.Id).ToList();
            var testGroup = _groups.Create("テストグループ");
            _groups.Assign(ids.Skip(1).Take(3), testGroup); // 2〜4 番目
            for (var i = 0; i < _allItems.Count; i++)
                _allItems[i].Tags = i % 3 == 0 ? ["改変"] : i % 3 == 1 ? ["ラフ"] : [];
            AvatarListCache.Save(AvatarListCache.Favorites, _user!.Id, _allItems.Take(12).Select((a, i) => new Avatar
            {
                Id = a.Id, Name = a.Avatar.Name, AuthorName = a.AuthorName, ThumbnailImageUrl = a.Avatar.ThumbnailImageUrl,
                FavoriteGroup = i < 5 ? "お気に入り 1" : "お気に入り 2",
            }).ToList());
            _settings.VrTab = AvatarTab.Own;
            _settings.VrSortKey = "recent_asc";
            _settings.VrGroupView = true;
            _settings.VrColumns = 3;

            var vr = _vr!;
            var panel = vr.PanelForTest;
            vr.SetOpenForTest(true);
            vr.ReloadForTest();
            for (var i = 0; i < 40 && panel.PageItems.Any(p => p.ThumbnailUrl is not null && p.Thumbnail is null); i++) await Task.Delay(100);
            Expect("今着ているアバターのページから開く", panel.PageItems.Any(p => p.IsCurrent), $"page={panel.Page + 1}/{panel.PageCount}");
            Expect("グループは 1 枚にまとまる", panel.Items.Count == _allItems.Count - 2 && panel.Items.Count(t => t.IsGroup) == 1,
                $"tiles={panel.Items.Count}");
            Expect("絞り込みの候補はタグ", panel.Chips.SequenceEqual(["ラフ", "改変"]) || panel.Chips.SequenceEqual(["改変", "ラフ"]), string.Join(",", panel.Chips));
            Save(panel.Render(), "panel-own-recent.png");

            // 当たり判定: 画像の座標でボタンやタイルの中心を押す
            Expect("閉じる", panel.HitTest(588, 44).Kind == VrPanel.HitKind.Close);
            Expect("タブ: 自分", panel.HitTest(114, 44) is { Kind: VrPanel.HitKind.Tab, Index: 0 });
            Expect("タブ: お気に入り", panel.HitTest(288, 44) is { Kind: VrPanel.HitKind.Tab, Index: 1 });
            Expect("タブ: パブリック", panel.HitTest(461, 44) is { Kind: VrPanel.HitKind.Tab, Index: 2 });
            Expect("最近使った順", panel.HitTest(100, 102).Kind == VrPanel.HitKind.SortRecent);
            Expect("名前順", panel.HitTest(240, 102).Kind == VrPanel.HitKind.SortName);
            Expect("グループ化", panel.HitTest(517, 102).Kind == VrPanel.HitKind.GroupToggle);
            Expect("チップ: すべて", panel.HitTest(40, 154) is { Kind: VrPanel.HitKind.Chip, Index: 0 });
            Expect("タイルの隙間は何もしない", panel.HitTest(219, 269).Kind == VrPanel.HitKind.None);

            // 名前順: 先頭ページの左上・右下
            vr.ClickForTest(240, 102);
            Expect("名前順に切り替わる", _settings.VrSortKey == "name_asc" && panel.SortKey == "name_asc");
            panel.TurnPage(-panel.Page);
            var first = panel.HitTest(121, 269);
            Expect("左上のタイル", first is { Kind: VrPanel.HitKind.Tile, Index: 0 }, first.ToString());
            var lastSlot = panel.PageItemCount - 1;
            var lastRect = panel.TileRect(lastSlot);
            var last = panel.HitTest(lastRect.X + lastRect.Width / 2, lastRect.Y + lastRect.Height / 2);
            Expect("ページの最後のタイル", last.Kind == VrPanel.HitKind.Tile && last.Index == lastSlot, last.ToString());
            Expect("先頭ページでは「前へ」は押せない", panel.HitTest(88, 770).Kind == VrPanel.HitKind.None);
            Expect(panel.PageCount > 1 ? "次へ" : "1 ページなら「次へ」は押せない",
                panel.HitTest(552, 770).Kind == (panel.PageCount > 1 ? VrPanel.HitKind.Next : VrPanel.HitKind.None));

            // 列数: 3 列では減らせない。増やすと 1 ページの数が増え、設定にも残る
            Expect("3 列では「−」は押せない", panel.HitTest(302, 102).Kind == VrPanel.HitKind.None);
            vr.ClickForTest(410, 102);
            Expect("「+」で 4 列", _settings.VrColumns == 4 && panel.Columns == 4 && panel.PageSize > 9, $"columns={panel.Columns} page={panel.PageSize}");
            vr.ClickForTest(302, 102);
            Expect("「−」で 3 列に戻る", _settings.VrColumns == 3 && panel.Columns == 3 && panel.PageSize == 9, $"columns={panel.Columns} page={panel.PageSize}");
            for (var cols = 4; cols <= VrPanel.MaxColumns; cols++)
            {
                panel.SetColumns(cols);
                var bottom = panel.TileRect(panel.PageSize - 1);
                Expect($"{cols} 列: タイルがパネルに収まる ({panel.PageSize} 体/ページ)", bottom.Right <= VrPanel.Width - 27 && bottom.Bottom <= 710,
                    $"right={bottom.Right:F0} bottom={bottom.Bottom:F0}");
            }
            Expect("10 列では「+」は押せない", panel.HitTest(410, 102).Kind == VrPanel.HitKind.None);
            Save(panel.Render(), "panel-10cols.png");
            panel.SetColumns(5);
            Save(panel.Render(), "panel-5cols.png");
            panel.SetColumns(3);

            // グループのタイルを押すと中身だけになり、「戻る」で元のページへ
            (double X, double Y) TileCenter(int index)
            {
                var r = panel.TileRect(index - panel.PageStart);
                return (r.X + r.Width / 2, r.Y + r.Height / 2);
            }
            panel.ShowPageOf(t => t.IsGroup);
            var groupIndex = panel.Items.ToList().FindIndex(t => t.IsGroup);
            var groupPage = panel.Page;
            var (gx, gy) = TileCenter(groupIndex);
            vr.ClickForTest(gx, gy);
            Expect("グループを開くと中身だけ", panel.OpenGroupName == "テストグループ" && panel.Items.Count == 3 && panel.Items.All(t => !t.IsGroup),
                $"open={panel.OpenGroupName} tiles={panel.Items.Count}");
            Expect("グループを開いている間は「戻る」", panel.HitTest(60, 154).Kind == VrPanel.HitKind.Back);
            for (var i = 0; i < 20 && panel.PageItems.Any(p => p.ThumbnailUrl is not null && p.Thumbnail is null); i++) await Task.Delay(100);
            Save(panel.Render(), "panel-group-open.png");
            vr.ClickForTest(60, 154);
            Expect("戻るとグループのタイルのページ", panel.OpenGroupName is null && panel.Page == groupPage && panel.PageItems.Any(t => t.IsGroup));
            for (var i = 0; i < 20 && panel.PageItems.Any(p => p.ThumbnailUrl is not null && p.Thumbnail is null); i++) await Task.Delay(100);
            Save(panel.Render(), "panel-group-tile.png");

            // グループ化をオフにすると、中身も 1 体ずつ並ぶ
            vr.ClickForTest(517, 102);
            Expect("グループ化オフ", !_settings.VrGroupView && panel.Items.Count == _allItems.Count && panel.Items.All(t => !t.IsGroup));
            vr.ClickForTest(517, 102);
            Expect("グループ化オン", _settings.VrGroupView && panel.Items.Any(t => t.IsGroup));

            // チップで絞り込む (「改変」)
            var chipX = Enumerable.Range(0, 150).Select(i => 28 + i * 4.0)
                .FirstOrDefault(x => panel.HitTest(x, 154) is { Kind: VrPanel.HitKind.Chip } h && h.Index > 0 && panel.Chips[h.Index - 1] == "改変");
            vr.ClickForTest(chipX, 154);
            Expect("タグで絞り込める", panel.Filter == "改変" && panel.Items.Count > 0
                && panel.Items.All(t => t.IsGroup ? t.Members.Any(m => m.Tags.Contains("改変")) : t.Tags.Contains("改変")),
                $"filter={panel.Filter} tiles={panel.Items.Count}");
            Save(panel.Render(), "panel-filter.png");

            // タブ: お気に入り (一覧キャッシュから。チップはお気に入りグループ)
            vr.ClickForTest(288, 44);
            Expect("お気に入りタブ", _settings.VrTab == AvatarTab.Favorites && panel.Filter is null && panel.Items.Count > 0
                && panel.Chips.SequenceEqual(["お気に入り 1", "お気に入り 2"]), $"tiles={panel.Items.Count} chips={string.Join(",", panel.Chips)}");
            for (var i = 0; i < 20 && panel.PageItems.Any(p => p.ThumbnailUrl is not null && p.Thumbnail is null); i++) await Task.Delay(100);
            Save(panel.Render(), "panel-favorites.png");
            // タブ: パブリック (プレビューでは空)
            vr.ClickForTest(461, 44);
            Expect("パブリックタブ (空)", _settings.VrTab == AvatarTab.Public && panel.Items.Count == 0);
            Save(panel.Render(), "panel-public-empty.png");
            vr.ClickForTest(114, 44);
            Expect("自分のタブに戻る", _settings.VrTab == AvatarTab.Own && panel.Items.Count > 0);

            panel.Status = "アバターチェンジ失敗: Kikyo 9";
            panel.StatusIsError = true;
            panel.Hover = new VrPanel.Hit(VrPanel.HitKind.Tile, panel.PageStart + 4);
            Save(panel.Render(), "panel-hover-error.png");
            panel.Status = "";
            panel.StatusIsError = false;
            panel.Hover = default;
            Save(VrPanel.RenderButton(panelOpen: false), "button.png");
            Save(VrPanel.RenderButton(panelOpen: true), "button-open.png");

            // 行列: SteamVR の 3x4 (列ベクトル) に直しても、点の移り先が変わらないこと
            var m = Matrix4x4.CreateRotationX(0.3f) * Matrix4x4.CreateRotationY(-1.1f) * Matrix4x4.CreateTranslation(0.2f, 1.5f, -0.4f);
            var h = VrOverlay.ToHmdForTest(m);
            var p = new Vector3(0.1f, -0.2f, 0.3f);
            var byHmd = new Vector3(h.m0 * p.X + h.m1 * p.Y + h.m2 * p.Z + h.m3, h.m4 * p.X + h.m5 * p.Y + h.m6 * p.Z + h.m7, h.m8 * p.X + h.m9 * p.Y + h.m10 * p.Z + h.m11);
            Expect("行列の変換 (Numerics → SteamVR)", Vector3.Distance(byHmd, Vector3.Transform(p, m)) < 1e-5f);
            Expect("行列の変換 (往復)", Vector3.Distance(Vector3.Transform(p, VrOverlay.ToNumericsForTest(h)), Vector3.Transform(p, m)) < 1e-5f);

            // 手首ボタン: 左手なら甲 (-X) を向き、手首より肘側 (+Z) にあること。右手は左右反転
            foreach (var hand in new[] { "left", "right" })
            {
                var s = new Settings { VrHand = hand, VrButtonAlongCm = 8 };
                var offset = VrOverlay.ButtonOffsetForTest(s);
                var normal = Vector3.TransformNormal(Vector3.UnitZ, offset);
                var up = Vector3.TransformNormal(Vector3.UnitY, offset);
                var pos = offset.Translation;
                report.AppendLine($"     {hand}: 位置 ({pos.X:F3}, {pos.Y:F3}, {pos.Z:F3}) m / 表の向き ({normal.X:F2}, {normal.Y:F2}, {normal.Z:F2}) / 画像の上 ({up.X:F2}, {up.Y:F2}, {up.Z:F2})");
                Expect($"{hand}: 表は腕の外側を向く", hand == "left" ? normal.X < -0.5f : normal.X > 0.5f);
                Expect($"{hand}: 表はやや上向き", normal.Y > 0.1f);
                Expect($"{hand}: 画像の上は手の側 (-Z)", up.Z < -0.9f);
                Expect($"{hand}: 腕時計 (0.13m) より 8cm 肘側", MathF.Abs(pos.Z - 0.21f) < 0.001f);
            }

            // 触れた点 → 画像の座標: 斜めに置いたパネルの右上寄りを触る
            var panelWorld = Matrix4x4.CreateRotationX(-0.26f) * Matrix4x4.CreateRotationY(0.8f) * Matrix4x4.CreateTranslation(0.3f, 1.2f, -0.5f);
            var w = VrOverlay.PanelWidthForTest;
            var hM = w * VrPanel.Height / VrPanel.Width;
            var touch = Vector3.Transform(new Vector3(w * 0.25f, hM * 0.25f, 0.01f), panelWorld); // 中心から右へ 1/4、上へ 1/4、1cm 手前
            var inside = VrOverlay.TouchPoint(panelWorld, w, VrPanel.Width, VrPanel.Height, touch, out var at);
            Expect("触れた点が画像の右上寄りに対応", inside && Math.Abs(at.X - 480) < 0.5 && Math.Abs(at.Y - 200) < 0.5, $"({at.X:F1}, {at.Y:F1})");
            var far = Vector3.Transform(new Vector3(0, 0, 0.05f), panelWorld); // 5cm 手前
            Expect("離れている点は触れていない", !VrOverlay.TouchPoint(panelWorld, w, VrPanel.Width, VrPanel.Height, far, out _));
            var outside = Vector3.Transform(new Vector3(w * 0.6f, 0, 0), panelWorld); // 面の外
            Expect("面の外は触れていない", !VrOverlay.TouchPoint(panelWorld, w, VrPanel.Width, VrPanel.Height, outside, out _));

            // SteamVR に渡す並び (RGBA) になっているか: 「グループ化: オン」のボタンの色 (今のアクセント色) の画素を見る
            var accent = App.AccentColor(dark: true);
            var rgba = panel.RenderRgba();
            var px = (102 * VrPanel.Width + 452) * 4; // (452, 102) = グループ化ボタンの左端寄り (文字の無いところ)
            Expect($"RGBA の並び (アクセント {accent})", rgba[px] == accent.R && rgba[px + 1] == accent.G && rgba[px + 2] == accent.B && rgba[px + 3] == 0xFF,
                $"{rgba[px]:X2} {rgba[px + 1]:X2} {rgba[px + 2]:X2} {rgba[px + 3]:X2}");
            var btn = VrPanel.ButtonRgba(panelOpen: false);
            var bAt = (6 * VrPanel.ButtonSize + 96) * 4; // 手首ボタンの輪の一番上 (アクセント色)
            Expect("手首ボタンも RGBA", btn[bAt] == accent.R && btn[bAt + 1] == accent.G && btn[bAt + 2] == accent.B, $"{btn[bAt]:X2} {btn[bAt + 1]:X2} {btn[bAt + 2]:X2}");
            var inner = (96 * VrPanel.ButtonSize + 40) * 4; // 輪の内側でアイコンの無いところ
            Expect("手首ボタンの下地は塗りつぶさず半透明", btn[inner + 3] < 0xFF && btn[inner + 2] < 0x40,
                $"{btn[inner]:X2} {btn[inner + 1]:X2} {btn[inner + 2]:X2} {btn[inner + 3]:X2}");
            Expect("手首ボタンの画像は作り直さない", ReferenceEquals(btn, VrPanel.ButtonRgba(panelOpen: false)));

            // 同じ画像なら SteamVR に渡し直さない (ちらつき対策)
            var lastSent = new byte[VrPanel.Width * VrPanel.Height * 4];
            var frame = panel.RenderRgba();
            Expect("初回は渡す", VrOverlay.CopyIfChanged(frame, lastSent, lastValid: false));
            Expect("同じ画像は渡さない", !VrOverlay.CopyIfChanged(panel.RenderRgba(), lastSent, lastValid: true));
            panel.Hover = new VrPanel.Hit(VrPanel.HitKind.Tile, panel.PageStart);
            Expect("ホバーが変われば渡す", VrOverlay.CopyIfChanged(panel.RenderRgba(), lastSent, lastValid: true));
            panel.Hover = default;
            Expect("繋ぎ直したあとは同じ画像でも渡す", VrOverlay.CopyIfChanged(panel.RenderRgba(), lastSent, lastValid: false));

            // 描き直し 1 回ぶんの負荷 (ホバーやサムネイルの到着のたびに走る)
            vr.RenderPanelBytesForTest();
            GC.Collect();
            var alloc0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int renders = 30;
            for (var i = 0; i < renders; i++) vr.RenderPanelBytesForTest();
            sw.Stop();
            var allocKb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / renders / 1024;
            report.AppendLine($"     パネルの描き直し: {sw.Elapsed.TotalMilliseconds / renders:F2} ms/回、確保 {allocKb} KB/回");

            // パネルの距離 (グリップ + スティック前後)
            Expect("遊びの範囲では動かない", VrOverlay.NextPanelDistance(0.55f, 0.2f, 0.1f) == 0.55f);
            Expect("前に倒すと奥へ", VrOverlay.NextPanelDistance(0.55f, 1f, 0.1f) > 0.55f);
            Expect("後ろに倒すと手前へ", VrOverlay.NextPanelDistance(0.55f, -1f, 0.1f) < 0.55f);
            Expect("奥の上限で止まる", VrOverlay.NextPanelDistance(1.49f, 1f, 0.5f) == VrOverlay.MaxPanelDistanceM);
            Expect("手前の下限で止まる", VrOverlay.NextPanelDistance(0.31f, -1f, 0.5f) == VrOverlay.MinPanelDistanceM);
            var headPos = new Vector3(0.2f, 1.6f, 0.1f);
            var fwd = Vector3.Normalize(new Vector3(1, 0, -1));
            foreach (var d in new[] { VrOverlay.MinPanelDistanceM, 1.0f })
            {
                var pose = VrOverlay.PanelPose(headPos, fwd, d);
                var flat = pose.Translation - headPos;
                flat.Y = 0;
                var face = Vector3.TransformNormal(Vector3.UnitZ, pose);
                Expect($"距離 {d:F2}m: 正面にその距離だけ離れて置かれる", MathF.Abs(flat.Length() - d) < 1e-4f && Vector3.Dot(Vector3.Normalize(flat), fwd) > 0.999f);
                Expect($"距離 {d:F2}m: 表がこちらを向く", Vector3.Dot(face, -fwd) > 0.9f);
            }
        }
        catch (Exception ex) { failures++; report.AppendLine("EXCEPTION: " + ex); }
        report.Insert(0, failures == 0 ? "RESULT: all passed\n" : $"RESULT: {failures} failed\n");
        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 誤操作でのグループ化対策の自己診断 (VRCAC_UI_PREVIEW_GROUPSAFETY=フォルダ)。
    /// ドラッグそのものは OS の処理なので再現せず、重ねたときの判定と「元に戻す」を直接呼んで確かめる。
    /// </summary>
    private async Task RunGroupSafetyTestAsync(string dir)
    {
        Left = -4000; Top = 0;
        var report = new System.Text.StringBuilder();
        var failures = 0;
        void Expect(string label, bool ok, string detail = "")
        {
            if (!ok) failures++;
            report.AppendLine($"{(ok ? "OK  " : "NG  ")}{label}{(detail.Length > 0 ? "  " + detail : "")}");
        }
        try
        {
            Directory.CreateDirectory(dir);
            await Task.Delay(500);
            var tiles = AvatarList.Items.OfType<AvatarItem>().ToList();

            // 重ねて待つと確定
            SetDropTarget(tiles[2]);
            ArmDropIfHeld();
            Expect("重ねた直後は確定しない (薄いハイライト)", !_dropArmed && tiles[2].IsDropPending && !tiles[2].IsDropTarget);
            await Task.Delay(DropArmDelay + TimeSpan.FromMilliseconds(100));
            ArmDropIfHeld();
            Expect("少し待つと確定 (はっきりしたハイライト)", _dropArmed && tiles[2].IsDropTarget && !tiles[2].IsDropPending);
            // 確定できる状態の合図: 枠を光が流れる (リスト表示とボックス表示の両方)
            System.Windows.Shapes.Rectangle? Light(AvatarItem item)
                => AvatarList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c
                    ? c.Template.FindName("light", c) as System.Windows.Shapes.Rectangle : null;
            void Shot(string name, AvatarItem item)
            {
                if (AvatarList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c) return;
                var pad = 12;
                var rtb = new RenderTargetBitmap((int)c.ActualWidth + pad * 2, (int)c.ActualHeight + pad * 2, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle((Brush)FindResource("PanelBrush"), null, new Rect(0, 0, rtb.Width, rtb.Height));
                    dc.DrawRectangle(new VisualBrush(c), null, new Rect(pad, pad, c.ActualWidth, c.ActualHeight));
                }
                rtb.Render(dv);
                using var fs = File.Create(Path.Combine(dir, name));
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                enc.Save(fs);
            }
            foreach (var view in new[] { "list", "grid" })
            {
                (view == "grid" ? ViewGrid : ViewList).IsChecked = true;
                AvatarList.UpdateLayout();
                await Task.Delay(300);
                var target = AvatarList.Items.OfType<AvatarItem>().ElementAt(2);
                SetDropTarget(target);
                await Task.Delay(DropArmDelay + TimeSpan.FromMilliseconds(100));
                ArmDropIfHeld();
                await Task.Delay(100);
                var light = Light(target);
                var perimeter = light is null ? 0 : RunningLight.Perimeter(light.ActualWidth, light.ActualHeight, light.RadiusX, light.StrokeThickness);
                var lap = light?.StrokeDashArray is { Count: 2 } d ? (d[0] + d[1]) * light.StrokeThickness : 0;
                Expect($"{view}: 確定できると枠に光が流れる", light is { Visibility: Visibility.Visible, HasAnimatedProperties: true },
                    $"visible={light?.Visibility} animated={light?.HasAnimatedProperties}");
                Expect($"{view}: 光の筋はちょうど 1 周に 1 本", perimeter > 0 && Math.Abs(lap - perimeter) < 0.5, $"1 周 {perimeter:F1} / 破線の繰り返し {lap:F1}");
                Shot($"light-{view}-1.png", target);
                await Task.Delay(350);
                Shot($"light-{view}-2.png", target);
                SetDropTarget(null);
                Expect($"{view}: 外れると光が消える", light is { Visibility: Visibility.Collapsed, HasAnimatedProperties: false });
            }
            ViewList.IsChecked = true;
            AvatarList.UpdateLayout();
            tiles = AvatarList.Items.OfType<AvatarItem>().ToList();

            SetDropTarget(tiles[2]);
            SetDropTarget(tiles[3]);
            Expect("別のタイルへ移ると待ち直し", !_dropArmed && !tiles[2].IsDropTarget && !tiles[2].IsDropPending && tiles[3].IsDropPending);
            SetDropTarget(null);
            Expect("離れるとハイライトが消える", !tiles[3].IsDropPending && !tiles[3].IsDropTarget);

            // 元に戻す
            string Groups() => string.Join(" / ", _groups.Groups.Select(g => $"{g.Name}:{string.Join(",", g.AvatarIds.Select(id => id[^2..]))}"));
            var before = Groups();
            PerformDrop(tiles[1], tiles[2]);
            var grouped = Groups();
            Expect("グループ化される", grouped != before && _groups.Groups.Count == 1, grouped);
            Expect("「元に戻す」が出る", UndoButton.Visibility == Visibility.Visible);
            await Task.Delay(300);
            var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            using (var fs = File.Create(Path.Combine(dir, "undo.png")))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                enc.Save(fs);
            }
            Expect("Ctrl+Z / 元に戻すで取り消せる", UndoGroupChange() && Groups() == before, Groups());
            Expect("取り消したら「元に戻す」は消える", UndoButton.Visibility != Visibility.Visible);
            Expect("2 回目は何もしない", !UndoGroupChange());
            Expect("取り消しはファイルにも残る", GroupStore.Load().Groups.Count == _groups.Groups.Count);

            // 別のメッセージが出たら取り消しの対象から外れる
            tiles = AvatarList.Items.OfType<AvatarItem>().ToList();
            PerformDrop(tiles[1], tiles[2]);
            SetStatus(StatusKind.Info, "ID コピー完了");
            Expect("別の操作のあとは取り消せない", !UndoGroupChange() && UndoButton.Visibility != Visibility.Visible);
        }
        catch (Exception ex) { failures++; report.AppendLine("EXCEPTION: " + ex); }
        report.Insert(0, failures == 0 ? "RESULT: all passed\n" : $"RESULT: {failures} failed\n");
        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>プレビュー用のダミーサムネ URL (実際には事前に入れたディスクキャッシュから読まれる)。</summary>
    internal static string PreviewThumbUrl(int index) => $"https://api.vrchat.cloud/api/1/file/file_preview{index:D4}/1/file";

    /// <summary>
    /// サムネイルの読み込み順の確認。一覧の中ほどへ飛んで少し待ち、
    /// 「どの番号の画像が入ったか」を VRCAC_UI_PREVIEW_REPORT に書いて終了する。
    /// 画面に出ているものが優先されていれば、飛んだ先が先に埋まる。
    /// </summary>
    private async Task RunThumbTestAsync()
    {
        Left = -4000; Top = 0;
        var reportPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_REPORT");
        var report = new System.Text.StringBuilder();
        try
        {
            await Task.Delay(700); // 初期レイアウトと先頭ぶんの読み込み
            var sv = VisualTree.FindDescendant<ScrollViewer>(AvatarList);
            var items = AvatarList.Items.OfType<AvatarItem>().ToList();
            // 画面に出ているもののうち、まだ画像が入っていない数 (これが 0 なら灰色の枠は見えない)
            int VisibleBlank() => VisibleIndexes().Where(i => i < items.Count)
                .Count(i => items[i].Thumbnail is null && items[i].ThumbnailUrl is not null);
            report.AppendLine($"items={items.Count} 展開済={Loaded(items).Count} 表示中の空白={VisibleBlank()}");

            // 一覧の中ほどへ一気に飛ぶ (スクロールバーを掴んで動かした状況)
            var before = Loaded(items).ToHashSet();
            sv?.ScrollToVerticalOffset((sv.ExtentHeight - sv.ViewportHeight) * 0.5);
            await Task.Delay(500);
            var after = Loaded(items);
            var added = after.Where(i => !before.Contains(i)).ToList();
            report.AppendLine($"中ほどへ飛んだあと: 展開済={after.Count} 新たに展開={added.Count} 表示中の空白={VisibleBlank()}");
            report.AppendLine("新たに展開した番号: " + string.Join(",", added));
            report.AppendLine("表示中の番号:       " + string.Join(",", VisibleIndexes()));

            // ウィンドウを閉じている (トレイ常駐) 間は、裏の埋めが止まっているか
            Hide();
            var hiddenAt = Loaded(items).Count;
            await Task.Delay(1200);
            report.AppendLine($"非表示中: {hiddenAt} -> {Loaded(items).Count} 枚 (増えなければ止まっている)");
            Show();
            Left = -4000; Top = 0;

            await Task.Delay(3000);
            report.AppendLine($"開き直したあと: 展開済={Loaded(items).Count}/{items.Count} 表示中の空白={VisibleBlank()}");
            report.AppendLine("展開した幅の内訳: " + string.Join(", ",
                items.GroupBy(i => i.ThumbnailWidth).OrderBy(g => g.Key).Select(g => $"{g.Key}px x{g.Count()}")));

            // 表示形式を変えると必要な幅が変わる。切り替えた瞬間に空白にならず、あとで大きい画像に入れ替わるか
            if (!IsGridView)
            {
                ViewGrid.IsChecked = true;
                AvatarList.UpdateLayout();
                await Task.Delay(50);
                report.AppendLine($"ボックスに切替えた直後: 表示中の空白={VisibleBlank()}");
                await Task.Delay(4000);
                report.AppendLine($"切替 4 秒後: 表示中の空白={VisibleBlank()}、展開した幅の内訳: " +
                    string.Join(", ", items.GroupBy(i => i.ThumbnailWidth).OrderBy(g => g.Key).Select(g => $"{g.Key}px x{g.Count()}")));

                // 端まで一気にスクロールしたときに、着いた先が空白のままにならないか
                sv?.ScrollToVerticalOffset(sv.ExtentHeight);
                AvatarList.UpdateLayout();
                await Task.Delay(600);
                report.AppendLine($"末尾へ飛んだ 0.6 秒後: 表示中の空白={VisibleBlank()}");
            }
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();

        static List<int> Loaded(List<AvatarItem> items)
            => items.Select((a, n) => (n, a)).Where(t => t.a.Thumbnail is not null).Select(t => t.n).ToList();
    }

    /// <summary>今実体化されている(= 画面に出ている / 先読みされた)項目の番号。</summary>
    private List<int> VisibleIndexes()
    {
        var containers = new List<ListViewItem>();
        containers.AddRange(VisualTree.Descendants<ListViewItem>(AvatarList));
        return containers.Select(c => AvatarList.ItemContainerGenerator.IndexFromContainer(c))
            .Where(i => i >= 0).OrderBy(i => i).ToList();
    }

    private static async Task CaptureWindowAsync(Window w, string path, int delayMs = 1000)
    {
        await Task.Delay(delayMs); // レイアウトとテーマ適用を待つ
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        Application.Current.Shutdown();
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
            var sv = VisualTree.FindDescendant<ScrollViewer>(AvatarList);
            void Snapshot(string label)
            {
                var realized = VisualTree.Descendants<ListViewItem>(AvatarList).Count();
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
            containers.AddRange(VisualTree.Descendants<ListViewItem>(AvatarList));
            var bad = containers.Count(c => c.Content is not AvatarItem || !ReferenceEquals(c.Content, c.DataContext));
            var dup = containers.Count - containers.Select(c => c.Content).Distinct().Count();
            report.AppendLine($"container integrity: total={containers.Count} bad={bad} duplicates={dup}");
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown();
    }



}
#endif
