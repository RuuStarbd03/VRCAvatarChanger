using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>
/// マウスホイールのスクロールをなめらかにする添付ビヘイビア。
/// 使い方: 対象(ListView や ScrollViewer)に local:SmoothScroll.IsEnabled="True" を付ける。
///
/// VR コントローラーのスティックなどは「離散的なホイールイベントの列」として届き、
/// しかも間隔が一定とは限らない(揺らぐ)。そこで、
/// - 速度を状態に持つ臨界減衰バネ(2 次)で目標へ追従し(イベントが来ても速度が跳ねない)、
/// - 入力速度は「直近 600ms の移動量の合計 ÷ 経過時間」で推定して(間隔の揺らぎに頑健)、
///   押している間はその速度で等速走行させる(フィードフォワード)。
/// 環境変数 VRCAC_WHEEL_LOG にファイルパスを入れると、入力イベントと毎フレームの位置を記録する(診断用)。
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    // ホイール 1 ノッチ (Delta=120) あたりのスクロール量 (px)
    private const double PixelsPerNotch = 120;
    // 入力速度の推定に使う窓 (ms)。この間の移動量から「押している速さ」を求める
    private const double WindowMs = 600;
    // 追従(整定)時間。単発ノッチは機敏に、連続入力(速度が乗っている)ほどなめらかに
    private const double MinSettleSec = 0.12;
    private const double MaxSettleSec = 0.40;
    private const double MaxFeedVel = 5000; // フィードフォワード速度の上限 (px/s)

    private sealed class State
    {
        public double Pos;     // 追従中の現在位置 (ScrollViewer のオフセットと同期)
        public double Vel;     // 現在速度 (px/s)
        public double Target;
        public double FeedVel; // 入力から推定した速度 (px/s、平滑化済み)
        public bool Running;
        public TimeSpan LastRenderTime;
        public long LastWheelMs;
        public readonly Queue<(long Ms, double Move)> Recent = new(); // 窓内の入力イベント
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

        var now = Environment.TickCount64;
        var move = -e.Delta / 120.0 * PixelsPerNotch;
        st.Recent.Enqueue((now, move));
        TrimWindow(st.Recent, now);
        st.LastWheelMs = now;
        Log('w', e.Delta);

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

    private static void TrimWindow(Queue<(long Ms, double Move)> q, long now)
    {
        while (q.Count > 0 && now - q.Peek().Ms > WindowMs) q.Dequeue();
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

            // 入力速度の推定: 窓内の移動量 ÷ 経過時間。イベントが 3 つ未満なら単発扱いで等速走行はしない
            var nowMs = Environment.TickCount64;
            TrimWindow(st.Recent, nowMs);
            double ffTarget = 0;
            if (st.Recent.Count >= 3)
            {
                var span = (nowMs - st.Recent.Peek().Ms) / 1000.0;
                if (span > 0.05) ffTarget = Math.Clamp(st.Recent.Sum(r => r.Move) / span, -MaxFeedVel, MaxFeedVel);
            }
            st.FeedVel += (ffTarget - st.FeedVel) * (1 - Math.Exp(-dt / 0.10));

            var target = Math.Clamp(st.Target, 0, sv.ScrollableHeight);
            // 速度が乗っているほど長くならす (バネの補正でかくつかないように)
            var activity = Math.Min(1, Math.Abs(st.FeedVel) / 200);
            var settle = MinSettleSec + (MaxSettleSec - MinSettleSec) * activity;
            var w = 4.0 / settle; // 臨界減衰バネの固有角速度 (整定時間 ≒ 4/ω)
            var inputActive = nowMs - st.LastWheelMs < 350;

            // 半陰的オイラー。ω·h が大きいと不安定になるのでサブステップで刻む
            var steps = Math.Max(1, (int)Math.Ceiling(w * dt / 0.5));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                // 入力中はフィードフォワードを制限しない: 目標はノッチ単位で階段状にしか進まないため、
                // 追い越しを禁じると次のイベントまでの間に追いついて完全に止まる (= かくつき)。
                // 追い越してもバネの引き戻しと釣り合って自然に頭打ちになる (先行量 ≒ 2·ff/ω ≈ 1 ノッチ)。
                // 入力が止まったら、目標に向かう成分だけ残し「残距離でちょうど止まれる速度」まで絞って減速する
                var toTarget = target - st.Pos;
                var ff = st.FeedVel;
                if (!inputActive)
                {
                    if (Math.Sign(ff) != Math.Sign(toTarget)) ff = 0;
                    else
                    {
                        var ffLimit = w * Math.Abs(toTarget) / 2;
                        ff = Math.Clamp(ff, -ffLimit, ffLimit);
                    }
                }
                var a = w * w * toTarget - 2 * w * (st.Vel - ff);
                st.Vel += a * h;
                st.Pos += st.Vel * h;
            }
            // 入力が止まったとき目標を通り過ぎていたら、引き戻さずその場を目標にして止める
            // (逆走はスクロールでは行き過ぎ分より目立つ)。先行して速度が落ちきっていることがあるので、
            // 向きの判定は瞬間速度ではなくスクロール方向 (FeedVel) を優先する
            var dir = Math.Abs(st.FeedVel) > 15 ? Math.Sign(st.FeedVel) : Math.Sign(st.Vel);
            if (!inputActive && dir != 0 &&
                Math.Sign(target - st.Pos) == -dir &&
                Math.Abs(target - st.Pos) < Math.Max(2 * PixelsPerNotch, Math.Abs(st.FeedVel) * 0.4))
            {
                st.Target = Math.Clamp(st.Pos, 0, sv.ScrollableHeight);
                target = st.Target;
            }
            st.Pos = Math.Clamp(st.Pos, 0, sv.ScrollableHeight);

            if (Math.Abs(target - st.Pos) < 0.5 && Math.Abs(st.Vel) < 15 && Math.Abs(st.FeedVel) < 15)
            {
                st.Pos = target;
                st.Vel = 0;
                StopFollowing(st);
            }
            sv.ScrollToVerticalOffset(st.Pos);
            Log('f', st.Pos);
        };
        CompositionTarget.Rendering += st.OnFrame;
    }

    private static void StopFollowing(State st)
    {
        if (st.OnFrame is not null) CompositionTarget.Rendering -= st.OnFrame;
        st.OnFrame = null;
        st.Running = false;
        _wheelLog?.Flush();
    }

    // ---------------- 診断ログ (VRCAC_WHEEL_LOG=ファイルパス) ----------------

    private static readonly string? WheelLogPath = Environment.GetEnvironmentVariable("VRCAC_WHEEL_LOG");
    private static System.IO.StreamWriter? _wheelLog;
    private static int _logLines;

    /// <summary>w = ホイールイベント (値は Delta) / f = 毎フレームの位置。1 列目は起動からの ms。</summary>
    private static void Log(char kind, double value)
    {
        if (WheelLogPath is null) return;
        try
        {
            _wheelLog ??= new System.IO.StreamWriter(WheelLogPath, append: false) { AutoFlush = false };
            _wheelLog.WriteLine($"{Environment.TickCount64}\t{kind}\t{value:F1}");
            if (++_logLines % 50 == 0) _wheelLog.Flush();
        }
        catch { }
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
