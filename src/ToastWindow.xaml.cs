using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VRCAvatarChanger;

/// <summary>
/// ホットキーでの着替えの結果を、VRChat の上に数秒だけ出す小さな通知。
/// プレイ中はメイン画面が見えないので、これが無いと成功したのか失敗したのか分からない。
/// クリックはすべて下(ゲーム)に通し、フォーカスも奪わない。
/// </summary>
public partial class ToastWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _hide;

    public ToastWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            App.ApplyTitleBarTheme(this);
            Win32.MakeClickThrough(Hwnd);
        };
        _hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        _hide.Tick += (_, _) => { _hide.Stop(); FadeOut(); };
    }

    public nint Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>領域 (物理ピクセル) の上部中央に出す。scale は表示先モニターの DPI スケール。</summary>
    internal void ShowMessage(string text, Win32.NativeRect areaPx, double scale, bool error)
    {
        Message.Text = text;
        ToastIcon.Text = error ? "\uEA39" : "\uE73E"; // Segoe Fluent Icons: ErrorBadge / CheckMark
        ToastIcon.Foreground = (Brush)FindResource(error ? "DangerBrush" : "AccentBrush");

        if (!IsVisible) Show();
        // 位置決めは実サイズが要るので、レイアウトを確定させてから
        UpdateLayout();
        var wPx = (int)Math.Round(ActualWidth * scale);
        var hPx = (int)Math.Round(ActualHeight * scale);
        var x = areaPx.Left + ((areaPx.Right - areaPx.Left) - wPx) / 2;
        var y = areaPx.Top + (int)Math.Round(56 * scale);
        Win32.SetWindowPosPx(Hwnd, x, y, wPx, hPx);

        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _hide.Stop();
        _hide.Start();
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { if (Root.Opacity == 0) Hide(); };
        Root.BeginAnimation(OpacityProperty, fade);
    }
}
