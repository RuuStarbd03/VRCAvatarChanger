using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>
/// 与えられた幅から高さを決める (幅 × Ratio) 入れ物。角は Radius で丸く切り抜く。ボックス表示のサムネイル枠に使う。
///
/// 以前は Height を自分の ActualWidth からバインディングで作っていたが、それだと
/// 「配置 → 幅が決まる → 高さが変わる → 計測し直し」が タイル 1 枚ごとに起き、一覧を入れ直すたびに
/// 計測が 30 回以上回っていた (500 体で 1 回 200ms ほど)。ここでは計測の時点で幅から高さを出すので、1 回で決まる。
/// </summary>
public sealed class AspectBox : Decorator
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(AspectBox), new FrameworkPropertyMetadata(0.75, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(AspectBox), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>高さ / 幅。</summary>
    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    /// <summary>切り抜く角の丸み。</summary>
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    private Size _clipSize;

    protected override Size MeasureOverride(Size constraint)
    {
        var width = constraint.Width;
        if (double.IsInfinity(width))
        {
            // 幅が決まっていない (並べ方の都合で無制限に測られた) ときは、中身の幅に合わせる
            Child?.Measure(constraint);
            width = Child?.DesiredSize.Width ?? 0;
        }
        var size = new Size(width, Math.Max(0, width * Ratio));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = new Size(arrangeSize.Width, Math.Max(0, arrangeSize.Width * Ratio));
        Child?.Arrange(new Rect(size));
        if (size != _clipSize)
        {
            // 大きさが変わったときだけ切り抜きを作り直す
            _clipSize = size;
            var clip = new RectangleGeometry(new Rect(size), Radius, Radius);
            clip.Freeze();
            Clip = clip;
        }
        return size;
    }
}
