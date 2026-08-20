using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>
/// UniformGrid の見た目(固定列数・等幅タイル・上から行で埋める)のまま、行単位で仮想化するパネル。
/// UniformGrid は仮想化に対応しておらず、ボックス表示でアバターが多いと全件分の UI 要素を作ってしまうため、
/// 画面に見えている行 + 前後 1 行だけを実体化する。タイルの高さは全行で同じ(幅から決まる)前提。
/// </summary>
public sealed class VirtualizingUniformGrid : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(VirtualizingUniformGrid),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>1 行あたりのタイル数。</summary>
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    // 行の高さ。タイルの画像部分は ActualWidth バインディング(配置後に確定)で高さが決まるため、
    // 実体化直後の計測値は低く出る。実測値に素直に追従すると計測のたびに行高が揺れて
    // レイアウトが収束しない(無限ループ)ので、幅から見積もった値を下限に「増える方向にだけ」補正する。
    private double _rowHeight = 160;
    private double _rowHeightItemWidth; // 見積もりに使ったタイル幅。幅(列数)が変わったら見積もり直す
    private Size _extent;
    private Size _viewport;
    private double _offset;

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren; // 先に触らないと ItemContainerGenerator が初期化されない (WPF の仕様)
        var generator = ItemContainerGenerator;
        var itemCount = ItemCount;
        var cols = Math.Max(1, Columns);

        if (itemCount == 0 || double.IsInfinity(availableSize.Width) || availableSize.Width <= 0)
        {
            if (children.Count > 0) { generator.RemoveAll(); RemoveInternalChildRange(0, children.Count); }
            UpdateScrollInfo(availableSize, new Size(0, 0));
            return new Size(0, 0);
        }

        var itemWidth = availableSize.Width / cols;
        if (Math.Abs(itemWidth - _rowHeightItemWidth) > 0.5)
        {
            _rowHeightItemWidth = itemWidth;
            _rowHeight = itemWidth * 0.75 + 60; // 4:3 の画像 + テキスト 2 行分の見込み。実測が上回れば補正される
        }
        var rows = (itemCount + cols - 1) / cols;
        var viewportHeight = double.IsInfinity(availableSize.Height) ? _viewport.Height : availableSize.Height;

        // 見えている行 + 前後 1 行を実体化の対象にする
        var firstRow = Math.Max(0, (int)(_offset / _rowHeight) - 1);
        var lastRow = Math.Min(rows - 1, (int)((_offset + viewportHeight) / _rowHeight) + 1);
        var firstIndex = firstRow * cols;
        var lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * cols - 1);

        CleanUpOutside(firstIndex, lastIndex);

        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        double measuredRowHeight = 0;
        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var i = firstIndex; i <= lastIndex; i++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child) break;
                if (newlyRealized || childIndex >= children.Count || !ReferenceEquals(children[childIndex], child))
                {
                    // 新規、またはリサイクルで返ってきた(ツリーから外れている)コンテナを所定の位置に入れる
                    if (childIndex >= children.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    // リサイクル分も Prepare し直して Content を新しい項目に差し替える
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                measuredRowHeight = Math.Max(measuredRowHeight, child.DesiredSize.Height);
            }
        }
        if (measuredRowHeight > _rowHeight) _rowHeight = measuredRowHeight;

        var extent = new Size(availableSize.Width, rows * _rowHeight);
        UpdateScrollInfo(availableSize, extent);
        return new Size(availableSize.Width, Math.Min(extent.Height, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var cols = Math.Max(1, Columns);
        var itemWidth = finalSize.Width / cols;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) continue;
            var row = itemIndex / cols;
            var col = itemIndex % cols;
            InternalChildren[i].Arrange(new Rect(col * itemWidth, row * _rowHeight - _offset, itemWidth, _rowHeight));
        }
        return finalSize;
    }

    /// <summary>
    /// 可視範囲から外れた実体化済みコンテナを片付ける。
    /// 破棄 (Remove) ではなくリサイクルに回し、スクロールで次の行が現れたときに
    /// テンプレートを組み立て直さず再利用する(行の実体化によるカクつきを抑える)。
    /// </summary>
    private void CleanUpOutside(int firstIndex, int lastIndex)
    {
        var generator = (IRecyclingItemContainerGenerator)ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(pos);
            if (itemIndex < firstIndex || itemIndex > lastIndex)
            {
                generator.Recycle(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                // ItemsSource の差し替え(絞り込みなど)。作り直しに備えて全部片付け、先頭から表示し直す
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
                _offset = 0;
                break;
        }
    }

    /// <summary>ScrollIntoView で未実体化の項目が指定されたとき、その行が見えるところまでスクロールする。</summary>
    protected override void BringIndexIntoView(int index)
    {
        var cols = Math.Max(1, Columns);
        var top = index / cols * _rowHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + _rowHeight > _offset + _viewport.Height) SetVerticalOffset(top + _rowHeight - _viewport.Height);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        // ScrollViewer の外で使われた場合など、高さの制約が無いときはスクロール不要として扱う
        if (double.IsInfinity(viewport.Height)) viewport = new Size(viewport.Width, extent.Height);
        if (viewport == _viewport && extent == _extent) return;
        _viewport = viewport;
        _extent = extent;
        _offset = Math.Max(0, Math.Min(_offset, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
    }

    // ---------------- IScrollInfo (縦スクロールのみ) ----------------

    private const double LineSize = 48;

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (offset == _offset) return;
        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(_offset - LineSize);
    public void LineDown() => SetVerticalOffset(_offset + LineSize);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - LineSize * 3);
    public void MouseWheelDown() => SetVerticalOffset(_offset + LineSize * 3);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        try
        {
            if (visual is UIElement el && el.IsDescendantOf(this))
            {
                var top = visual.TransformToAncestor(this).Transform(new Point(0, 0)).Y + _offset;
                var bottom = top + Math.Max(rectangle.Height, _rowHeight);
                if (top < _offset) SetVerticalOffset(top);
                else if (bottom > _offset + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
            }
        }
        catch { /* レイアウト途中で座標が取れないことがある。スクロールしないだけ */ }
        return rectangle;
    }
}
