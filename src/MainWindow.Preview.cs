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

        // 見た目確認: VRCAC_UI_PREVIEW_SHOT=path でウィンドウを画面外に置いたまま PNG に描画して終了する
        // (実画面をキャプチャしないので、ゲーム中でも邪魔にならない)。SETTINGS=1 なら設定オーバーレイを開いた状態で撮る
        var shotPath = Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SHOT");
        if (Environment.GetEnvironmentVariable("VRCAC_UI_PREVIEW_SETTINGS") == "1") OpenSettings();
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

    private static async Task CaptureWindowAsync(Window w, string path)
    {
        await Task.Delay(1000); // レイアウトとテーマ適用を待つ
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
