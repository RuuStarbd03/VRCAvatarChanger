using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>
/// VR パネルの小さな動き (CSS のトランジションに相当するもの)。どれも操作した直後の 0.3 秒以内で終わり、
/// 動いている間だけ VrOverlay が毎フレーム描き直す (IsAnimating)。止まっている間は今までどおり描き直さない。
///   ・ホバー: 背景の色がふわっと変わる (指した所は明るく、離れた所は元へ)
///   ・押したとき: 押したボタン / タイルが一瞬縮んで、弾んで戻る (PC 画面のボタンと同じ)
///   ・タイルを選んだ (1 回目): 白い枠の外へ光の輪が広がって消える
///   ・タブ / 絞り込みのチップ: 選んでいる印 (アクセント色) が前の場所から滑って移る
/// 毎フレームの描き直しで画像が抜けない渡し方 (GPU) のときだけ動かす (AnimationsEnabled)。
/// Windows の「アニメーションを表示する」がオフなら動かさない。
/// </summary>
internal sealed partial class VrPanel
{
    private const double HoverFadeMs = 150;
    private const double PressShrinkMs = 70;   // 縮むまで
    private const double PressReturnMs = 230;  // 弾んで戻るまで
    private const double PressScale = 0.93;
    private const double SelectGlowMs = 320;
    private const double SlideMs = 200;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double NowMs => Clock.Elapsed.TotalMilliseconds;
    private const double Never = -1e9;

    /// <summary>動きを付けるか。生データで渡しているとき (毎フレーム差し替えるとちらつく) は VrOverlay が false にする。</summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled && SystemParameters.ClientAreaAnimation;
        set => _animationsEnabled = value;
    }
    private bool _animationsEnabled = true;

    /// <summary>動きの途中か (VrOverlay はこの間だけ毎フレーム描き直す)。</summary>
    public bool IsAnimating
    {
        get
        {
            if (!AnimationsEnabled) return false;
            var now = NowMs;
            return now - _hoverAt < HoverFadeMs || now - _pressAt < PressShrinkMs + PressReturnMs
                   || now - _selectedAt < SelectGlowMs || now - _tabAt < SlideMs || now - _chipAt < SlideMs;
        }
    }

    // ---- ホバー ----
    private Hit _hover;
    private Hit _prevHover;
    private double _hoverAt = Never;

    public Hit Hover
    {
        get => _hover;
        set
        {
            if (value == _hover) return;
            _prevHover = _hover;
            _hover = value;
            _hoverAt = NowMs;
        }
    }

    /// <summary>その場所のホバーの濃さ (0〜1)。指し始めた所は 0 → 1、離れた所は 1 → 0 へ。</summary>
    private double HoverAmount(Hit hit)
    {
        if (hit == default) return 0;
        var t = AnimationsEnabled ? Progress(_hoverAt, HoverFadeMs) : 1;
        if (hit == _hover) return EaseOut(t);
        if (hit == _prevHover) return 1 - EaseOut(t);
        return 0;
    }

    /// <summary>ホバーの濃さに合わせて、ふだんの色とホバーの色を混ぜたブラシ。</summary>
    private Brush HoverFill(Hit hit, Brush normal) => HoverFill(HoverAmount(hit), normal);

    private static Brush HoverFill(double amount, Brush normal)
    {
        if (amount <= 0) return normal;
        if (amount >= 1) return TileHoverBrush;
        var a = ((SolidColorBrush)normal).Color;
        var b = ((SolidColorBrush)TileHoverBrush).Color;
        return Frozen(Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * amount), (byte)(a.R + (b.R - a.R) * amount),
            (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount)));
    }

    // ---- 押したとき ----
    private Hit _pressed;
    private double _pressAt = Never;

    /// <summary>押した (VrOverlay が押したことにしたとき呼ぶ)。押した所を一瞬縮める。</summary>
    public void NotifyPressed(Hit hit)
    {
        if (hit == default) return;
        _pressed = hit;
        _pressAt = NowMs;
    }

    /// <summary>押した所なら、縮んで戻る途中の大きさ (1 = そのまま) で描き始める。描き終えたら <see cref="PopPress"/>。</summary>
    private bool PushPress(DrawingContext dc, Hit hit, Rect r)
    {
        if (!AnimationsEnabled || hit != _pressed) return false;
        var t = NowMs - _pressAt;
        double scale;
        if (t < PressShrinkMs) scale = 1 - (1 - PressScale) * EaseOut(t / PressShrinkMs);
        else if (t < PressShrinkMs + PressReturnMs) scale = PressScale + (1 - PressScale) * EaseOutBack((t - PressShrinkMs) / PressReturnMs);
        else return false;
        dc.PushTransform(new ScaleTransform(scale, scale, r.X + r.Width / 2, r.Y + r.Height / 2));
        return true;
    }

    private static void PopPress(DrawingContext dc, bool pushed)
    {
        if (pushed) dc.Pop();
    }

    // ---- タイルを選んだ ----
    private int _selected = -1;
    private double _selectedAt = Never;

    /// <summary>
    /// 1 回押して選んだタイル (一覧全体での番号、-1 = なし)。同じタイルをもう一度押すとチェンジする
    /// (PC のダブルクリックと同じく、うっかり触れただけで着替えないように)。
    /// </summary>
    public int Selected
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            _selectedAt = value >= 0 ? NowMs : Never;
        }
    }

    /// <summary>選んだタイルの枠の外へ広がって消える光の輪。</summary>
    private void DrawSelectGlow(DrawingContext dc, Rect r, double radius)
    {
        if (!AnimationsEnabled) return;
        var t = Progress(_selectedAt, SelectGlowMs);
        if (t >= 1) return;
        var e = EaseOut(t);
        var grow = 2 + 8 * e;
        var pen = new Pen(Frozen(Color.FromArgb((byte)(200 * (1 - e)), 0xFF, 0xFF, 0xFF)), 3 * (1 - e) + 1);
        pen.Freeze();
        dc.DrawRoundedRectangle(null, pen, new Rect(r.X - grow, r.Y - grow, r.Width + grow * 2, r.Height + grow * 2), radius + grow, radius + grow);
    }

    // ---- 選んでいる印が滑って移る ----
    private string _tab = AvatarTab.Own;
    private int _tabFrom;
    private double _tabAt = Never;

    public string Tab
    {
        get => _tab;
        set
        {
            if (value == _tab) return;
            _tabFrom = Array.IndexOf(Tabs, _tab);
            _tab = value;
            _tabAt = NowMs;
        }
    }

    /// <summary>選んでいるタブの印の場所 (前のタブから滑ってくる途中ならその場所)。</summary>
    private Rect TabIndicatorRect(int selectedIndex)
    {
        var to = TabRect(selectedIndex);
        var t = AnimationsEnabled ? Progress(_tabAt, SlideMs) : 1;
        if (t >= 1 || _tabFrom < 0) return to;
        return Lerp(TabRect(_tabFrom), to, EaseOutCubic(t));
    }

    private Rect? _chipFrom;     // 選び直す前に選んでいたチップの場所
    private double _chipAt = Never;

    /// <summary>絞り込みを選び直すとき、前の印の場所を覚えておく (Filter の set から呼ぶ)。</summary>
    private void BeginChipSlide(string? next)
    {
        if (next == _filter || _chipLayout is not { } old) return;
        _chipFrom = old.FirstOrDefault(c => c.Selected && c.Hit.Kind == HitKind.Chip) is { Hit.Kind: HitKind.Chip } prev ? prev.Rect : null;
        _chipAt = NowMs;
    }

    /// <summary>選んでいるチップの印の場所 (前のチップから滑ってくる途中ならその場所)。</summary>
    private Rect ChipIndicatorRect(Rect to)
    {
        var t = AnimationsEnabled ? Progress(_chipAt, SlideMs) : 1;
        if (t >= 1 || _chipFrom is not { } from) return to;
        return Lerp(from, to, EaseOutCubic(t));
    }

    // ---- 計算 ----

    private static double Progress(double startMs, double durationMs) => Math.Clamp((NowMs - startMs) / durationMs, 0, 1);

    private static double EaseOut(double t) => 1 - (1 - t) * (1 - t);

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>少し行き過ぎてから戻る (PC 画面のボタンの BackEase と同じ感じ)。</summary>
    private static double EaseOutBack(double t)
    {
        const double c1 = 1.2;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static Rect Lerp(Rect a, Rect b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);
}
