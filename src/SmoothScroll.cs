using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VRCAvatarChanger;

/// <summary>
/// マウスホイールのスクロールをイージング付きでなめらかにする添付ビヘイビア。
/// 使い方: 対象(ListView や ScrollViewer)に local:SmoothScroll.IsEnabled="True" を付ける。
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    // ホイール 1 ノッチ (Delta=120) あたりのスクロール量 (px)
    private const double PixelsPerNotch = 120;
    private static readonly Duration AnimDuration = new(TimeSpan.FromMilliseconds(240));

    // アニメーションの目標位置。NaN = 未設定(現在位置から始める)
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.RegisterAttached(
        "IsAnimating", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false));

    // この属性プロパティをアニメーションし、値の変化で実際にスクロールさせる
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) => (d as ScrollViewer)?.ScrollToVerticalOffset((double)e.NewValue)));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        el.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject host) return;
        var sv = FindScrollViewer(host);
        if (sv is null || sv.ScrollableHeight <= 0) return;
        e.Handled = true;

        // 連続してホイールを回したときは、前回の目標位置から加算して勢いを保つ
        var target = (double)sv.GetValue(TargetProperty);
        var animating = (bool)sv.GetValue(IsAnimatingProperty);
        var from = sv.VerticalOffset;
        if (double.IsNaN(target) || !animating) target = from;
        target = Math.Clamp(target - e.Delta / 120.0 * PixelsPerNotch, 0, sv.ScrollableHeight);
        sv.SetValue(TargetProperty, target);
        sv.SetValue(IsAnimatingProperty, true);

        var anim = new DoubleAnimation(from, target, AnimDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => sv.SetValue(IsAnimatingProperty, false);
        sv.BeginAnimation(AnimatedOffsetProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
