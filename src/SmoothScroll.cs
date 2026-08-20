using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>
/// マウスホイールのスクロールをなめらかにする添付ビヘイビア。
/// 使い方: 対象(ListView や ScrollViewer)に local:SmoothScroll.IsEnabled="True" を付ける。
///
/// VR コントローラーのスティックなどは「一定間隔の離散ホイールイベント」として届くため、
/// 単純な指数追従(1 次)では次のイベントが来るまでに速度が減衰し、脈打ってかくつく。
/// ここでは速度を状態に持つ臨界減衰バネ(2 次)で目標へ追従させ、さらに入力イベントの間隔を
/// 推定して追従時間を自動調整する(間隔が広い入力ほど長めにならして等速に近づける)。
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    // ホイール 1 ノッチ (Delta=120) あたりのスクロール量 (px)
    private const double PixelsPerNotch = 120;
    // 追従(整定)時間 = 入力間隔 × この係数。大きいほどなめらか、小さいほど機敏
    private const double SettleFactor = 3.0;
    private const double MinSettleSec = 0.12; // マウスの単発ノッチはこの機敏さで
    private const double MaxSettleSec = 0.60; // 間隔の広い入力でもこれ以上はもたつかせない
    private const double MaxFeedVel = 5000;   // フィードフォワード速度の上限 (px/s)

    private sealed class State
    {
        public double Pos;            // 追従中の現在位置 (ScrollViewer のオフセットと同期)
        public double Vel;            // 現在速度 (px/s)
        public double Target;
        public double FeedVel;        // 入力から推定した速度 (px/s)。押している間の等速走行に使う
        public bool Running;
        public TimeSpan LastRenderTime;
        public long LastWheelMs;
        public double IntervalMs = 50; // 入力イベント間隔の推定値 (EMA)
        public EventHandler? OnFrame;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScroll), new PropertyMetadata(null));

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

        if (sv.GetValue(StateProperty) is not State st)
        {
            st = new State();
            sv.SetValue(StateProperty, st);
        }

        // 入力間隔を推定する。500ms 以上あいたら別のスクロール操作の始まりとみなして機敏側に戻す
        var now = Environment.TickCount64;
        var gap = now - st.LastWheelMs;
        st.LastWheelMs = now;
        var move = -e.Delta / 120.0 * PixelsPerNotch;
        if (gap is > 0 and < 500)
        {
            st.IntervalMs = st.IntervalMs * 0.6 + gap * 0.4;
            // 入力の平均速度 (px/s)。スティック押しっぱなしの間、この速度で等速走行させる
            var instVel = Math.Clamp(move / (gap / 1000.0), -MaxFeedVel, MaxFeedVel);
            st.FeedVel = Math.Sign(instVel) == Math.Sign(st.FeedVel)
                ? st.FeedVel * 0.5 + instVel * 0.5
                : instVel * 0.5; // 向きが変わったら引きずらない
        }
        else
        {
            st.IntervalMs = 50;
            st.FeedVel = 0;
        }

        // 停止中は現在位置から始め、追従中は目標に加算して勢いを保つ
        if (!st.Running)
        {
            st.Pos = sv.VerticalOffset;
            st.Vel = 0;
            st.Target = st.Pos;
        }
        st.Target = Math.Clamp(st.Target + move, 0, sv.ScrollableHeight);
        StartFollowing(sv, st);
    }

    private static void StartFollowing(ScrollViewer sv, State st)
    {
        if (st.Running) return;
        st.Running = true;
        st.LastRenderTime = TimeSpan.Zero;
        st.OnFrame = (_, args) =>
        {
            if (!st.Running) return;
            if (!sv.IsLoaded) { StopFollowing(st); return; }

            var now = ((RenderingEventArgs)args).RenderingTime;
            var dt = st.LastRenderTime == TimeSpan.Zero ? 1.0 / 60 : (now - st.LastRenderTime).TotalSeconds;
            if (dt <= 0) return; // 同一フレームで複数回呼ばれることがある
            st.LastRenderTime = now;
            dt = Math.Min(dt, 0.05);

            // スクロールバー操作などで外からオフセットが動いたら、そこから追従し直す
            if (Math.Abs(sv.VerticalOffset - st.Pos) > 2) { st.Pos = sv.VerticalOffset; st.Vel = 0; }

            var target = Math.Clamp(st.Target, 0, sv.ScrollableHeight);
            var settle = Math.Clamp(st.IntervalMs / 1000.0 * SettleFactor, MinSettleSec, MaxSettleSec);
            var w = 4.0 / settle; // 臨界減衰バネの固有角速度 (整定時間 ≒ 4/ω)

            // 入力が続いている間はフィードフォワード速度をフルに効かせ、途切れたらなめらかに抜く
            var sinceInput = (Environment.TickCount64 - st.LastWheelMs) / 1000.0;
            var inputActive = sinceInput < st.IntervalMs / 1000.0 * 1.2 + 0.03;
            if (!inputActive) st.FeedVel *= Math.Exp(-dt / 0.08);

            // 半陰的オイラー。ω·h が大きいと不安定になるのでサブステップで刻む
            var steps = Math.Max(1, (int)Math.Ceiling(w * dt / 0.5));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                // 入力中はフル速度で先読みし、入力が止まったら「残距離でちょうど止まれる速度」
                // (= 平衡状態の速度) までに制限して目標へ滑らかに減速する。
                // 制限が無いと入力終了後に目標を通り過ぎてから引き戻される(ゴム跳ね)
                var toTarget = target - st.Pos;
                var ff = st.FeedVel;
                if (Math.Sign(ff) != Math.Sign(toTarget)) ff = 0;
                else if (!inputActive)
                {
                    var ffLimit = w * Math.Abs(toTarget) / 2;
                    ff = Math.Clamp(ff, -ffLimit, ffLimit);
                }
                var a = w * w * toTarget - 2 * w * (st.Vel - ff);
                st.Vel += a * h;
                st.Pos += st.Vel * h;
            }
            // 入力が止まった後にわずかに目標を通り過ぎたら、引き戻さずその場を目標にして止める
            // (数十 px の逆走はスクロールでは行き過ぎ分より目立つ)
            if (!inputActive && st.Vel != 0 &&
                Math.Sign(target - st.Pos) == -Math.Sign(st.Vel) && Math.Abs(target - st.Pos) < PixelsPerNotch)
            {
                st.Target = Math.Clamp(st.Pos, 0, sv.ScrollableHeight);
                target = st.Target;
            }
            st.Pos = Math.Clamp(st.Pos, 0, sv.ScrollableHeight);

            if (Math.Abs(target - st.Pos) < 0.5 && Math.Abs(st.Vel) < 15)
            {
                st.Pos = target;
                st.Vel = 0;
                StopFollowing(st);
            }
            sv.ScrollToVerticalOffset(st.Pos);
        };
        CompositionTarget.Rendering += st.OnFrame;
    }

    private static void StopFollowing(State st)
    {
        if (st.OnFrame is not null) CompositionTarget.Rendering -= st.OnFrame;
        st.OnFrame = null;
        st.Running = false;
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
