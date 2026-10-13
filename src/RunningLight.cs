using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace VRCAvatarChanger;

/// <summary>
/// 枠に沿って光の筋を流し続ける添付ビヘイビア。Rectangle に付けて IsActive を結び付ける。
/// ドラッグで重ねたタイルが「離せばグループ化される」状態になったことを、枠を一周する光で知らせるために使う。
///
/// 光の筋は破線の 1 本で、破線の繰り返しの長さを枠の 1 周にそろえるので、大きさ (リスト / ボックス表示、列数) に
/// よらず常に 1 本だけが流れる。周りには筋と同じ色のにじみを付ける (配色を切り替えても、付けた時点の色に追従)。
/// Windows の設定でアニメーションを切っている場合は、流さずに枠全体を光らせるだけにする。
/// </summary>
public static class RunningLight
{
    /// <summary>光の筋の長さ (DIP)。</summary>
    private const double LightLength = 96;

    /// <summary>1 周にかける時間。</summary>
    private static readonly Duration Lap = new(TimeSpan.FromSeconds(1.1));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(RunningLight), new PropertyMetadata(false, OnIsActiveChanged));

    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject d, bool value) => d.SetValue(IsActiveProperty, value);

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Rectangle r) return;
        if (e.NewValue is true)
        {
            r.SizeChanged += OnSizeChanged;
            r.Visibility = Visibility.Visible;
            r.Effect = new DropShadowEffect
            {
                Color = (r.Stroke as SolidColorBrush)?.Color ?? Colors.White,
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = 1,
            };
            Start(r);
        }
        else
        {
            r.SizeChanged -= OnSizeChanged;
            r.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            r.Visibility = Visibility.Collapsed;
            r.Effect = null; // 効果は描画の負担になるので、光らせていない間は外す
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Rectangle r) Start(r); // 列数を変えるなどで大きさが変わったら、1 周の長さを測り直す
    }

    /// <summary>
    /// 角丸の四角の 1 周の長さ (DIP)。線の太さの半分だけ内側を通るので、その分を引いた大きさで測る。
    /// </summary>
    internal static double Perimeter(double width, double height, double radius, double thickness)
    {
        var w = width - thickness;
        var h = height - thickness;
        if (w <= 0 || h <= 0) return 0;
        var rad = Math.Clamp(radius - thickness / 2, 0, Math.Min(w, h) / 2);
        return 2 * (w + h) - 8 * rad + 2 * Math.PI * rad;
    }

    private static void Start(Rectangle r)
    {
        var t = Math.Max(0.5, r.StrokeThickness);
        var perimeter = Perimeter(r.ActualWidth, r.ActualHeight, r.RadiusX, t);
        if (perimeter <= 0) return; // まだ配置されていない。大きさが決まったら SizeChanged で始まる

        if (!SystemParameters.ClientAreaAnimation)
        {
            // アニメーションを切っている環境では動かさず、枠全体を光らせる
            r.StrokeDashArray = null;
            r.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            return;
        }

        // 破線の長さは線の太さを 1 とした単位で指定する。筋 1 本 + 残りの空白 = ちょうど 1 周
        var lap = perimeter / t;
        var dash = Math.Min(LightLength / t, lap / 2);
        r.StrokeDashArray = new DoubleCollection { dash, lap - dash };
        r.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(0, -lap, Lap) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
