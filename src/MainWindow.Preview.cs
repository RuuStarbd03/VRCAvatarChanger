#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VRCAvatarChanger;

// UI 確認用のプレビューモードと自己診断 (Debug ビルド限定)。
// 環境変数 VRCAC_UI_PREVIEW=1 で API を叩かずにダミーデータでメイン画面を表示する。
public partial class MainWindow
{
    private void ShowUiPreview()
    {
        _user = new CurrentUser { DisplayName = "Preview User", CurrentAvatar = "avtr_00000000-0000-4000-8000-000000000001" };
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
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
                AuthorName = "preview_author",
                ReleaseStatus = i % 2 == 0 ? "private" : "public",
                CreatedAt = DateTimeOffset.Now.AddDays(-i * 3),
                UpdatedAt = DateTimeOffset.Now.AddDays(-i),
            }));
        ShowListState(loading: Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_STATE") == "loading");
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_STATE") == "empty") _allItems.Clear();
        ApplyFilter();
        if (withThumbs) QueueThumbnails(_allItems.ToList(), CancellationToken.None); // 実際の読み込みと同じ経路を通す
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
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_THUMBTEST") == "1") _ = RunThumbTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_PERFTEST") == "1") _ = RunPerfTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SOAKTEST") == "1") _ = RunSoakTestAsync();
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_GRIDTEST") == "1") _ = RunGridTestAsync();

        // 見た目確認: VRCAC_UI_PREVIEW_SHOT=path でウィンドウを画面外に置いたまま PNG に描画して終了する
        // (実画面をキャプチャしないので、ゲーム中でも邪魔にならない)。SETTINGS=1 なら設定オーバーレイを開いた状態で撮る
        var shotPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SHOT");
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SETTINGS") == "1") OpenSettings();
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
            toast.ShowMessage("Selestia に着替えました",
                new Win32.NativeRect { Left = -4400, Top = 0, Right = -4000, Bottom = 400 }, 1.0, error: false);
            if (!string.IsNullOrEmpty(shotPath)) _ = CaptureWindowAsync(toast, shotPath);
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
            _quick = new QuickPickWindow(QuickChangeAsync, () => { }, SaveQuickSortKey);
            _quick.OpenAt(new Win32.NativeRect { Left = -4400, Top = 0, Right = -4000, Bottom = 760 }, 1.0,
                FlatAvatarItems(), _settings.RecentAvatars, _settings.QuickSortKey, Hotkey.Parse(_settings.QuickHotkey));
            if (!string.IsNullOrEmpty(shotPath)) _ = CaptureWindowAsync(_quick, shotPath);
        }
        else if (!string.IsNullOrEmpty(shotPath))
        {
            Left = -4000; Top = 0;
            _ = CaptureWindowAsync(this, shotPath);
        }
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

            var sv = FindDescendant<ScrollViewer>(AvatarList);
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
            Measure("一覧キャッシュ 保存", () => AvatarListCache.Save(AvatarListCache.Own, "usr_perf", avatars), 10);
            Measure("一覧キャッシュ 読み込み", () => AvatarListCache.Load(AvatarListCache.Own, "usr_perf"), 10);
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION: " + ex); }
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report.ToString());
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
            var sv = FindDescendant<ScrollViewer>(AvatarList);
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
        CollectDescendants(AvatarList, containers);
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
}
#endif
