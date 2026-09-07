using System.Windows.Threading;

namespace VRCAvatarChanger;

/// <summary>
/// 連続して起きる出来事 (検索欄の打鍵、スライダーのドラッグ) を、静止してから 1 回だけ処理する。
/// UI スレッドのタイマーなので、処理は UI スレッドで走る。
/// </summary>
internal sealed class Debounce
{
    private readonly DispatcherTimer _timer;

    public Debounce(TimeSpan quiet, Action action)
    {
        _timer = new DispatcherTimer { Interval = quiet };
        _timer.Tick += (_, _) => { _timer.Stop(); action(); };
    }

    /// <summary>出来事が起きた。静止時間を測り直す。</summary>
    public void Trigger()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>待っている処理を取りやめる (終了時など)。</summary>
    public void Cancel() => _timer.Stop();
}
