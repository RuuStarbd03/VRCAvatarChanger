using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace VRCAvatarChanger;

// グループ: 衣装違いなどをまとめるアプリ独自のグループ操作と、ドラッグ&ドロップによるグループ化。
public partial class MainWindow
{
    private AvatarGroup? _openGroup;

    /// <summary>ON: グループを 1 枚にまとめて表示 / OFF: すべて個別に表示。</summary>
    private bool GroupViewOn => GroupToggle.IsChecked == true;

    private void GroupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // OFF にしたら、開いているグループ表示も解除して通常一覧に戻す
        if (!GroupViewOn) ResetOpenGroup();
        _settings.GroupView = GroupViewOn;
        SaveSettings();
        ApplyFilter();
    }

    private void OpenGroup(AvatarGroup group)
    {
        _openGroup = group;
        GroupBar.Visibility = Visibility.Visible;
        ApplyFilter();
    }

    /// <summary>開いているグループの表示をやめる (一覧の作り直しはしない)。</summary>
    private void ResetOpenGroup()
    {
        _openGroup = null;
        GroupBar.Visibility = Visibility.Collapsed;
    }

    private void CloseGroup()
    {
        var g = _openGroup;
        ResetOpenGroup();
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
            if (_openGroup == group) ResetOpenGroup();
        }
    }

    private void MenuAssignGroup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAvatar is not { } item) return;
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
        if (SelectedAvatar is not { } item) return;
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

    private static AvatarItem? ItemAt(DependencyObject? origin)
        => VisualTree.FindAncestor<ListViewItem>(origin)?.DataContext as AvatarItem;

    private void AvatarList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(AvatarList);
        _dragSource = ItemAt(e.OriginalSource as DependencyObject);
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
        StartDragScroll();
        try { DragDrop.DoDragDrop(AvatarList, new DataObject(typeof(AvatarItem), src), DragDropEffects.Move); }
        finally { StopDragScroll(); }
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
        var target = ItemAt(e.OriginalSource as DependencyObject);
        var ok = src is not null && target is not null && !ReferenceEquals(src, target) && !(src.IsGroup && target.IsGroup && src.Group == target.Group);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(ok ? target : null);
        _dragScrollY = e.GetPosition(AvatarList).Y;
        e.Handled = true;
    }

    private void AvatarList_DragLeave(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        _dragScrollY = null;
    }

    // ---------------- ドラッグ中の自動スクロール ----------------
    // ドラッグ中は OS のドラッグ処理に入るのでホイールが効かない。
    // 下の方にいるアバターへ重ねられるよう、一覧の上端 / 下端に近づけたらその向きへ送る

    private const double DragScrollZone = 56;      // 端からこの距離 (DIP) に入ったら送り始める
    private const double DragScrollMaxSpeed = 1600; // 端に着けたときの速さ (DIP/秒)

    private DispatcherTimer? _dragScrollTimer;
    private readonly Stopwatch _dragScrollClock = new();
    private double? _dragScrollY; // 一覧の上端からのカーソル位置。一覧の外に出たら null

    private void StartDragScroll()
    {
        _dragScrollY = null;
        _dragScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => DragScrollTick(), Dispatcher) { IsEnabled = false };
        _dragScrollClock.Restart();
        _dragScrollTimer.Start();
    }

    private void StopDragScroll()
    {
        _dragScrollTimer?.Stop();
        _dragScrollY = null;
    }

    private void DragScrollTick()
    {
        var dt = _dragScrollClock.Elapsed.TotalSeconds;
        _dragScrollClock.Restart();
        if (_dragScrollY is not { } y || VisualTree.FindDescendant<ScrollViewer>(AvatarList) is not { ScrollableHeight: > 0 } sv) return;
        // 端に近いほど速く (ゾーンの入口で 0、端で最大)。ゆっくり探したいときは入口付近に置けばよい
        var depth = y < DragScrollZone ? -(DragScrollZone - y)
            : y > AvatarList.ActualHeight - DragScrollZone ? y - (AvatarList.ActualHeight - DragScrollZone)
            : 0;
        if (depth == 0) return;
        var ratio = Math.Clamp(depth / DragScrollZone, -1, 1);
        sv.ScrollToVerticalOffset(sv.VerticalOffset + ratio * Math.Abs(ratio) * DragScrollMaxSpeed * Math.Min(dt, 0.05));
    }

    private void AvatarList_Drop(object sender, DragEventArgs e)
    {
        var src = e.Data.GetData(typeof(AvatarItem)) as AvatarItem;
        var target = ItemAt(e.OriginalSource as DependencyObject);
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
        _groups.Assign(srcIds, dest);
        if (srcGroup is not null && srcGroup != dest) _groups.Delete(srcGroup);

        SetStatus(StatusKind.Success, message);
        ApplyFilter();
        if (AvatarList.Items.OfType<AvatarItem>().FirstOrDefault(a => a.Group == dest) is { } tile) AvatarList.SelectedItem = tile;
    }
}
