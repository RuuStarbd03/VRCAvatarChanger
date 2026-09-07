using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VRCAvatarChanger;

/// <summary>
/// タスクトレイのアイコン (Shell_NotifyIcon を直接呼ぶ)。
/// 以前は WinForms の NotifyIcon を使っていたが、そのためだけに WinForms 一式 (自己完結 exe で 20MB 超) を
/// 同梱することになるので、必要な API だけを呼ぶ形にした。メッセージは親ウィンドウ (MainWindow) の
/// ウィンドウプロシージャで受け取る。ウィンドウは隠れていてよい (メッセージは届く)。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint IconId = 1;

    private readonly Window _owner;
    private readonly nint _hwnd;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private nint _hIcon;
    private string _tip;
    private bool _added;

    /// <summary>左ダブルクリック。</summary>
    public event Action? DoubleClick;

    /// <summary>右クリック (またはメニューキー) で出すメニュー。null なら出さない。</summary>
    public ContextMenu? Menu { get; set; }

    public TrayIcon(Window owner, string tip)
    {
        _owner = owner;
        _tip = tip;
        // まだ表示していないウィンドウ (--tray 起動) でもハンドルは作れる
        _hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("ウィンドウのハンドルを取得できませんでした");
        _source.AddHook(WndProc);
        // エクスプローラーが再起動するとトレイが消えるので、その通知で置き直す
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _hIcon = LoadAppIcon();
        Add();
    }

    /// <summary>実行ファイルに埋め込まれたアイコン (小さいほう)。取れなければ Windows の既定のアプリアイコン。</summary>
    private static nint LoadAppIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) && ExtractIconExW(path, 0, out _, out var small, 1) > 0 && small != 0) return small;
        return LoadIconW(0, 32512 /* IDI_APPLICATION */);
    }

    private void Add()
    {
        var data = MakeData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        data.szTip = _tip;
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_added) Log.Warn("トレイアイコンを追加できませんでした");
    }

    private NOTIFYICONDATAW MakeData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage && (uint)wParam == IconId)
        {
            switch ((int)lParam)
            {
                case WM_LBUTTONDBLCLK:
                    DoubleClick?.Invoke();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // エクスプローラーが立ち上がり直した。アイコンを置き直す
            Add();
        }
        return 0;
    }

    private void ShowMenu()
    {
        if (Menu is null) return;
        // 手前に出しておかないと、メニューの外をクリックしても閉じない (Win32 のトレイメニューと同じ作法)
        SetForegroundWindow(_hwnd);
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = MakeData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        _source.RemoveHook(WndProc);
        if (_hIcon != 0) { DestroyIcon(_hIcon); _hIcon = 0; }
    }

    // ---- Win32 ----

    private const uint NIM_ADD = 0, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIconW(nint hInstance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint hIcon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
}
