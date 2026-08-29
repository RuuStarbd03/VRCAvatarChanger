using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VRCAvatarChanger;

// VRChat のプレイ中に効くホットキー (既定: Shift+1 でクイック着替えのオーバーレイ)。
// キーボードフックは「割り当てたキーの組み合わせが押されたか」の判定だけに使い、それ以外のキーは素通しする。
// 入力内容の記録・送信は一切しない。反応するのは VRChat (またはこのオーバーレイ) が手前のときだけ。
// 何のキーに何を割り当てるかは MainWindow.Hotkeys.cs にある。
public partial class MainWindow
{
    private nint _kbHook;
    private Win32.LowLevelKeyboardProc? _kbProc; // GC に回収されないよう保持する
    private QuickPickWindow? _quick;
    private nint _vrchatHwnd;

    /// <summary>ctor から一度だけ呼ぶ。設定が ON ならフックを張る (プレビューでは張らない)。</summary>
    private void InitQuickOverlay()
    {
        RebuildHotkeys();
        if (_settings.QuickOverlay && !_preview) InstallKeyHook();
        Closed += (_, _) => { UninstallKeyHook(); _quick?.Close(); _toast?.Close(); };
    }

    /// <summary>設定からの切り替えを適用する (保存・フックの張り替え)。</summary>
    internal void SetQuickOverlay(bool enabled)
    {
        if (_settings.QuickOverlay == enabled) return;
        _settings.QuickOverlay = enabled;
        if (!_preview) _settings.Save();
        if (enabled)
        {
            InstallKeyHook();
            var quick = Hotkey.Parse(_settings.QuickHotkey);
            SetStatus(StatusKind.Info, quick.IsSet
                ? $"ホットキー: オン。VRChat のプレイ中に {quick.Display} でクイック着替えを開きます"
                : "ホットキー: オン。割り当ては設定の「ホットキー」から行えます");
        }
        else
        {
            UninstallKeyHook();
            _quick?.CloseOverlay(refocus: false);
            SetStatus(StatusKind.Info, "ホットキー: オフ");
        }
    }

    private void InstallKeyHook()
    {
        if (_kbHook != 0) return;
        _kbProc = KeyHookProc;
        _kbHook = Win32.SetWindowsHookExW(Win32.WhKeyboardLl, _kbProc, Win32.GetModuleHandleW(null), 0);
        if (_kbHook == 0) _kbProc = null; // 張れない環境ではメイン画面から使ってもらう
    }

    private void UninstallKeyHook()
    {
        if (_kbHook == 0) return;
        Win32.UnhookWindowsHookEx(_kbHook);
        _kbHook = 0;
        _kbProc = null;
    }

    private nint KeyHookProc(int nCode, nint wParam, nint lParam)
    {
        // Alt を含む組み合わせは WM_SYSKEYDOWN で来る。
        // ここはキーを押すたびに (VRChat 以外を触っているときも) 通るので、安い判定から順に篩う:
        //   割り当てのあるキーか → 修飾キーが一致するか → VRChat が手前か (プロセス照会があり一番重い)
        if (nCode >= 0 && (wParam == Win32.WmKeydown || wParam == Win32.WmSysKeydown) && _hotkeys.Count > 0)
        {
            var key = Hotkey.FromVirtualKey(Marshal.ReadInt32(lParam));
            if (HasHotkeyFor(key)
                && FindHotkey(new Hotkey(Win32.CurrentModifiers(), key)) is { } hit
                && ShouldHandleQuickKey())
            {
                Dispatcher.BeginInvoke(() => RunHotkey(hit));
                return 1; // 割り当てたキーは VRChat 側に渡さない
            }
        }
        return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    /// <summary>VRChat かこのオーバーレイが手前のときだけホットキーに反応する。</summary>
    private bool ShouldHandleQuickKey()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == 0) return false;
        if (_quick is not null && _quick.IsVisible && fg == _quick.Hwnd) return true;
        _ = Win32.GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!p.ProcessName.Equals(QuickTargetProcess, StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }
        _vrchatHwnd = fg; // 表示位置の基準と、閉じたときにフォーカスを返す先
        return true;
    }

    // Debug ビルドでは環境変数 VRCAC_QUICK_PROCESS で対象プロセス名を差し替えられる (notepad 等で動作確認するため)
    private static readonly string QuickTargetProcess =
#if DEBUG
        Environment.GetEnvironmentVariable("VRCAC_QUICK_PROCESS") ??
#endif
        "VRChat";

    private void ToggleQuickOverlay()
    {
        _quick ??= new QuickPickWindow(QuickChangeAsync, RefocusVRChat, SaveQuickSortKey);
        if (_quick.IsVisible) { _quick.CloseOverlay(refocus: true); return; }
        // PerMonitorV2 では WPF の DIP 変換の倍率が「どのモニターにいるか」で変わり、
        // メインウィンドウと VRChat が別モニターだと座標がずれる。
        // そのため位置は物理ピクセルのまま渡し、倍率は VRChat のいるモニターの DPI から取る
        _quick.OpenAt(QuickOverlayAreaPx(), Win32.ScaleOf(_vrchatHwnd), FlatAvatarItems(),
            _settings.RecentAvatars, _settings.QuickSortKey, Hotkey.Parse(_settings.QuickHotkey));
    }

    private void SaveQuickSortKey(string key)
    {
        _settings.QuickSortKey = key;
        if (!_preview) _settings.Save();
    }

    private void RefocusVRChat()
    {
        if (_vrchatHwnd != 0) Win32.SetForegroundWindow(_vrchatHwnd);
    }

    /// <summary>
    /// オーバーレイを出す領域 (物理ピクセル)。VRChat のウィンドウ (クライアント領域) が取れればその中の右端、
    /// 取れないか小さすぎる場合はモニターの作業領域にフォールバックする。
    /// </summary>
    private Win32.NativeRect QuickOverlayAreaPx()
    {
        if (_vrchatHwnd != 0 && Win32.ClientAreaPx(_vrchatHwnd) is { } c
            && c.Right - c.Left >= 480 && c.Bottom - c.Top >= 320)
            return c;
        return Win32.WorkAreaPx(_vrchatHwnd);
    }

    /// <summary>今の一覧をグループ展開してフラットにしたアバターだけの列。</summary>
    private List<AvatarItem> FlatAvatarItems()
    {
        var flat = new List<AvatarItem>();
        foreach (var item in _allItems)
        {
            if (item.IsGroup) flat.AddRange(item.Members);
            else flat.Add(item);
        }
        // グループの中身はメイン画面の現在マーク更新の対象外なので、ここで付け直す
        var cur = _user?.CurrentAvatar;
        foreach (var i in flat) i.IsCurrent = i.Id == cur;
        // サムネ未取得のもの (グループの中身など) を優先で読み込む。これから見えるものなので後回しにしない
        foreach (var i in flat) RequestThumbnail(i);
        return flat;
    }

    private async Task<bool> QuickChangeAsync(AvatarItem item) => await ChangeAvatarAsync(item.Id, item.Name);
}

/// <summary>クイック着替え・ホットキーで使う Win32 API。</summary>
internal static class Win32
{
    public const int WhKeyboardLl = 13;
    public const nint WmKeydown = 0x0100;
    public const nint WmSysKeydown = 0x0104; // Alt を押しながらのキー

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;

    /// <summary>今押されている修飾キー。フックはキー 1 つ分しか届かないので、修飾キーはここで見る。</summary>
    public static ModifierKeys CurrentModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (IsDown(VkControl)) modifiers |= ModifierKeys.Control;
        if (IsDown(VkShift)) modifiers |= ModifierKeys.Shift;
        if (IsDown(VkMenu)) modifiers |= ModifierKeys.Alt;
        if (IsDown(VkLWin) || IsDown(VkRWin)) modifiers |= ModifierKeys.Windows;
        return modifiers;

        static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;
    }

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo mi);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out NativeRect r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref NativePoint p);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    /// <summary>ウィンドウのクライアント領域 (画面座標の物理ピクセル)。取れなければ null。</summary>
    public static NativeRect? ClientAreaPx(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var r)) return null;
        var origin = new NativePoint { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return null;
        return new NativeRect
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + (r.Right - r.Left),
            Bottom = origin.Y + (r.Bottom - r.Top),
        };
    }

    /// <summary>ウィンドウのあるモニター (取れなければプライマリ)。</summary>
    private static nint MonitorOf(nint hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        return mon != 0 ? mon : MonitorFromWindow(0, 1 /* MONITOR_DEFAULTTOPRIMARY */);
    }

    /// <summary>ウィンドウのあるモニターの作業領域 (物理ピクセル)。</summary>
    public static NativeRect WorkAreaPx(nint hwnd)
    {
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfoW(MonitorOf(hwnd), ref mi)) return mi.Work;
        return new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }; // ここまで来ることは実質ない
    }

    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint hMonitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>ウィンドウのあるモニターの DPI スケール (100% = 1.0)。</summary>
    public static double ScaleOf(nint hwnd)
    {
        try
        {
            if (GetDpiForMonitor(MonitorOf(hwnd), 0 /* MDT_EFFECTIVE_DPI */, out var dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch { }
        return 1.0;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(nint hWnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(nint hWnd, int index, int value);

    /// <summary>
    /// クリックを下のウィンドウ (ゲーム) に通し、フォーカスも奪わないようにする。
    /// ゲームの上に出す通知が操作の邪魔をしないために使う。
    /// </summary>
    public static void MakeClickThrough(nint hwnd)
    {
        const int GwlExStyle = -20;
        const int WsExTransparent = 0x00000020, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
        var ex = GetWindowLongW(hwnd, GwlExStyle);
        SetWindowLongW(hwnd, GwlExStyle, ex | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>物理ピクセル指定でウィンドウを移動・リサイズする (Z オーダー・表示状態は変えない)。</summary>
    public static void SetWindowPosPx(nint hwnd, int x, int y, int w, int h)
        => SetWindowPos(hwnd, 0, x, y, w, h, 0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect r);

    /// <summary>ウィンドウの外枠 (画面座標の物理ピクセル)。取れなければ null。</summary>
    public static NativeRect? WindowRectPx(nint hwnd)
        => GetWindowRect(hwnd, out var r) ? r : null;

    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ClipCursor(nint rect);

    /// <summary>
    /// ゲームなど他プロセスが手前でも自分のウィンドウを前面化する。
    /// (通常の SetForegroundWindow はフォアグラウンド権限がないと無視されるため、
    /// 手前スレッドに入力状態を一時的に共有してから前面化する)
    ///
    /// VRChat はデスクトップモードでマウスを掴んでいるので、掴みも外す。
    /// 外さないと、前面に出ていてもマウスの動きがゲーム側の視点移動に取られる。
    /// </summary>
    /// <returns>実際に前面になったか。</returns>
    public static bool FocusWindow(nint hwnd)
    {
        var fg = GetForegroundWindow();
        var fgThread = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
        var cur = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        // 入力状態を共有している間だけ、キーボードとマウスの行き先もこちらに向けられる
        SetActiveWindow(hwnd);
        SetFocus(hwnd);
        if (attached) AttachThreadInput(cur, fgThread, false);
        ClipCursor(0); // ゲームがマウスを閉じ込めていることがあるので外す
        return GetForegroundWindow() == hwnd;
    }
}
