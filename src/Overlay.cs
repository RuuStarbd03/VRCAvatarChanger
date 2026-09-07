using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VRCAvatarChanger;

/// <summary>
/// アプリ内オーバーレイ (設定・アバターの詳細) の開閉。フェードとカードの拡縮を同じ動きに揃える。
/// 閉じるアニメーションの途中で開き直された場合は、閉じ切ったときに Collapsed にしない。
/// </summary>
internal static class Overlay
{
    private static readonly HashSet<Grid> Opened = [];

    /// <summary>表示して開くアニメーションを始める。すでに出ていれば動きだけやり直す。</summary>
    public static void Open(Grid overlay, ScaleTransform cardScale)
    {
        if (overlay.Visibility != Visibility.Visible)
        {
            overlay.Opacity = 0;
            cardScale.ScaleX = cardScale.ScaleY = 0.96;
            overlay.Visibility = Visibility.Visible;
        }
        Opened.Add(overlay);
        Animate(overlay, cardScale, open: true, onClosed: null);
    }

    /// <summary>閉じるアニメーションを始め、閉じ切ったら Collapsed にして onClosed を呼ぶ。出ていなければ何もしない。</summary>
    public static void Close(Grid overlay, ScaleTransform cardScale, Action? onClosed = null)
    {
        if (overlay.Visibility != Visibility.Visible) return;
        Opened.Remove(overlay);
        Animate(overlay, cardScale, open: false, onClosed);
    }

    private static void Animate(Grid overlay, ScaleTransform cardScale, bool open, Action? onClosed)
    {
        var dur = TimeSpan.FromMilliseconds(open ? 180 : 130);
        var fade = new DoubleAnimation(open ? 1 : 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (!open) fade.Completed += (_, _) =>
        {
            if (Opened.Contains(overlay)) return; // 閉じている間に開き直された
            overlay.Visibility = Visibility.Collapsed;
            onClosed?.Invoke();
        };
        overlay.BeginAnimation(UIElement.OpacityProperty, fade);

        var scale = new DoubleAnimation(open ? 1 : 0.96, dur)
        {
            EasingFunction = open
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }
}
