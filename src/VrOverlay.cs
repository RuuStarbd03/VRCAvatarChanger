using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Valve.VR;

namespace VRCAvatarChanger;

/// <summary>VR パネルの見せ方。タブ・並び順・グループ化・絞り込み・開いているグループ。</summary>
internal readonly record struct VrView(string Tab, string SortKey, bool GroupView, string? Filter, AvatarGroup? OpenGroup);

/// <summary>
/// VR パネルに並べるアバターの出どころ (MainWindow が実装する)。
/// メイン画面で今開いているタブとは関係なく、VR パネル側で選んだタブの一覧を返す。
/// </summary>
internal interface IVrAvatarSource
{
    /// <summary>タブの一覧 (グループにまとめる前のアバター)。まだ手元に無ければ null (<see cref="LoadAsync"/> で取りに行く)。</summary>
    IReadOnlyList<AvatarItem>? ItemsOf(string tab);

    /// <summary>手元に無いタブの一覧を VRChat から取る。取れなければ例外。</summary>
    Task LoadAsync(string tab);

    /// <summary>絞り込みの候補 (お気に入り: お気に入りグループ / それ以外: タグ)。</summary>
    List<string> FilterValuesOf(IReadOnlyList<AvatarItem> items, string tab);

    /// <summary>絞り込み・並べ替え・グループ化をして、パネルに並べるタイルにする。今着ているものには印を付ける。</summary>
    List<AvatarItem> Arrange(IReadOnlyList<AvatarItem> items, VrView view);

    /// <summary>グループがまだあるか (VR パネルで開いている間に PC 側で解除されることがある)。</summary>
    bool GroupExists(AvatarGroup group);

    Task<bool> ChangeAsync(AvatarItem item);
    void RequestThumbnail(AvatarItem item);
    void SaveSettings();
}

/// <summary>
/// VR (SteamVR) での着替え。PC の Shift+1 の代わりに、手首のボタンからアバター選択パネルを開く。
///
/// ・SteamVR と VRChat の両方が動いているときだけ SteamVR に繋ぐ (こちらから SteamVR を起動することはない)。
/// ・手首ボタンはコントローラーに貼り付け、手首を見たときだけ現れる (普段は視界の邪魔をしない)。
///   OVR Toolkit などの腕時計と並べて使えるよう、腕時計の位置から肘側へずらして置く (ずらす量は設定で変えられる)。
/// ・押し方は 2 通り: 反対の手のコントローラーで触れる / SteamVR のレーザーで指してトリガー。
/// ・パネルは開いた時点の顔の正面に置き、その場に固定する (手を動かしても追いかけてこない)。
/// ・PC のクイック着替えとは別物。タブ・並び順・グループ化・絞り込みは VR パネルの中で切り替え、
///   PC の画面を触らずに済むようにする (選んだタブ・並び順・グループ化は設定に残す)。
///
/// 見た目と当たり判定は VrPanel が受け持つ。ここは SteamVR とのやりとりと、手・頭の位置の計算だけ。
/// SteamVR の API は UI スレッドのタイマーからだけ呼ぶ (スレッドをまたがない)。
/// </summary>
internal sealed class VrOverlay : IDisposable
{
    private const string ButtonKey = "vrcavatarchanger.wrist";
    private const string PanelKey = "vrcavatarchanger.panel";
    private const float ButtonWidthM = 0.05f;
    private const float PanelWidthM = 0.5f;

    // ---- 手首ボタンの置き場所 (コントローラー基準、m) ----
    // コントローラーの座標は -Z が指す向き、+Z が手首・肘の側、+Y が上 (親指側)、+X が右。
    // 腕時計 (手の甲側の手首) のおおよその位置の推測値。コントローラーの種類で原点が違うので、合わなければ設定で詰める。
    private const float WatchBackM = 0.13f;    // コントローラーの原点から手首まで (+Z)
    private const float WatchLiftM = 0.04f;    // 腕の中心から手首の甲の表面まで
    private const float WatchRollDeg = 70;     // 甲の向き: 真上 (0°) から腕の外側へ倒す角度。握ったとき手の甲は外を向く

    // ---- 触れて押す判定 ----
    private const float PokeTipM = 0.03f;      // 押す側のコントローラーの原点から、押す点までの距離 (-Z = 指す向き)
    private const float PokeDepthM = 0.025f;   // 面からこの距離以内に入ったら「触れた」
    private static readonly TimeSpan PressCooldown = TimeSpan.FromMilliseconds(400);

    // ---- パネルの置き場所 (開いた時点の頭の位置から) ----
    private const float DefaultPanelDistanceM = 0.55f;
    // グリップを握ってスティックを前後に倒すと、パネルを奥 / 手前へ動かせる。近すぎ・遠すぎで見失わないよう上限を設ける
    public const float MinPanelDistanceM = 0.3f;
    public const float MaxPanelDistanceM = 1.5f;
    private const float PanelMoveSpeed = 0.8f;    // スティックをいっぱいに倒したときの速さ (m/秒)
    private const float StickDeadZone = 0.25f;    // これより小さい傾きは無視 (スティックの戻りきらない揺れ)
    private const float PanelDropM = 0.15f;    // 目線より少し下
    private const float PanelTiltDeg = 15;     // 下に置くぶん、上を向けて顔に向ける

    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();

    // パネルの描き直しは 1 回で 2MB を SteamVR に渡すので、続けて起きる変化はまとめる
    private static readonly TimeSpan HoverRenderInterval = TimeSpan.FromMilliseconds(33);  // レーザーを動かしたとき (30 回/秒まで)
    private static readonly TimeSpan ThumbRenderInterval = TimeSpan.FromMilliseconds(150); // サムネイルが 1 枚ずつ届くとき

    private static readonly ETrackedControllerRole[] Hands = [ETrackedControllerRole.LeftHand, ETrackedControllerRole.RightHand];

    private readonly Settings _settings;
    private readonly IVrAvatarSource _source;

    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _frame = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly VrPanel _panel = new();
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private readonly Dictionary<(ulong Overlay, uint Device), bool> _touching = [];

    private bool _initialized; // OpenVR.Init が通った (Shutdown が要る)
    private bool _connected;   // オーバーレイまで作り終えた
    private bool _checking;
    private bool _vrchatRunning;
    private bool _initFailureLogged;
    private ulong _button;
    private ulong _panelHandle;
    private (uint Device, string Hand, double Along, double Height)? _placement; // 手首ボタンを最後に貼り付けた条件。変わったら貼り直す
    private Matrix4x4 _buttonOffset;  // その条件での、コントローラーから見た手首ボタンの位置
    private uint _leftHand = OpenVR.k_unTrackedDeviceIndexInvalid;   // 左右のコントローラーの機器番号。
    private uint _rightHand = OpenVR.k_unTrackedDeviceIndexInvalid;  // 毎フレーム問い合わせず、接続・持ち替えのときだけ調べ直す
    private float _buttonAlpha;
    private float _sentAlpha = -1;    // 最後に SteamVR へ送った透明度 (同じ値を送り直さない)
    private bool _buttonShown;
    private bool _panelOpen;
    private Matrix4x4 _panelWorld;
    private Matrix4x4 _panelInverse;       // 触れた点をパネルの座標に直すための逆行列 (置き直したときだけ計算する)
    private Vector3 _panelAnchor;          // パネルを開いたときの頭の位置 (距離はここから測る)
    private Vector3 _panelForward;         // そのときの正面 (水平)
    private float _panelDistance = DefaultPanelDistanceM; // 変えた距離は、アプリを閉じるまで次に開いたときも使う
    private readonly Dictionary<uint, int> _stickAxis = [];  // コントローラーごとの、スティック (無ければトラックパッド) の軸番号
    private readonly Stopwatch _frameClock = new();
    private bool _panelDirty;
    private DateTime _lastRender;
    private readonly BitmapSource?[] _renderedThumbs = new BitmapSource?[VrPanel.PageSize]; // 描いたときのサムネイル
    private DateTime _lastPress;
    private bool _busy;
    private int _fileFlip;
    // パネルの中身の状態 (タブ・並び順・グループ化は設定に持つ。絞り込みと開いているグループはその場限り)
    private string? _filter;
    private AvatarGroup? _openGroup;
    private string? _loadingTab;           // VRChat から一覧を取っている最中のタブ

    public VrOverlay(Settings settings, IVrAvatarSource source)
    {
        _settings = settings;
        _source = source;
        _watch.Tick += async (_, _) => await WatchAsync();
        _frame.Tick += (_, _) => Frame();
    }

    /// <summary>SteamVR と VRChat の起動を見張り始める。</summary>
    public void Start()
    {
        _watch.Start();
        WatchAsync().Forget();
    }

    /// <summary>見張りをやめ、SteamVR からも切断する。</summary>
    public void Stop()
    {
        _watch.Stop();
        Disconnect(null);
    }

    public void Dispose() => Stop();

    // ---------------- 接続 / 切断 ----------------

    private async Task WatchAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            // プロセスの列挙は全プロセスを見るので UI スレッドの外で
            var (steamVr, vrchat) = await Task.Run(() => (IsRunning("vrserver"), IsRunning("VRChat")));
            if (!_watch.IsEnabled) return; // 確認している間に止められた
            _vrchatRunning = vrchat;
            if (steamVr && vrchat && !_connected) Connect();
            else if (_connected && !(steamVr && vrchat)) Disconnect(steamVr ? "VRChat が終了したため" : "SteamVR が終了したため");
        }
        catch (Exception ex) { Log.Debug("SteamVR / VRChat の起動を確認できませんでした (次の周期で再確認します)", ex); }
        finally { _checking = false; }
    }

    private static bool IsRunning(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    private void Connect()
    {
        try
        {
            if (!OpenVR.IsRuntimeInstalled()) return;
            var err = EVRInitError.None;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
            if (err != EVRInitError.None)
            {
                // SteamVR の起動途中などで一時的に失敗することがある。次の周期でまた試すので、記録は初回だけ
                if (!_initFailureLogged) Log.Warn($"SteamVR に接続できませんでした ({err})。3 秒ごとに再試行します");
                _initFailureLogged = true;
                return;
            }
            _initialized = true;
            var overlay = OpenVR.Overlay;
            Check(overlay.CreateOverlay(ButtonKey, "AvatarChanger", ref _button), "手首ボタンの作成");
            overlay.SetOverlayWidthInMeters(_button, ButtonWidthM);
            SetupMouse(_button, VrPanel.ButtonSize, VrPanel.ButtonSize);
            UploadButton(panelOpen: false);

            Check(overlay.CreateOverlay(PanelKey, "AvatarChanger アバター選択", ref _panelHandle), "パネルの作成");
            overlay.SetOverlayWidthInMeters(_panelHandle, PanelWidthM);
            SetupMouse(_panelHandle, VrPanel.Width, VrPanel.Height);
            overlay.SetOverlayFlag(_panelHandle, VROverlayFlags.SendVRDiscreteScrollEvents, true);

            RefreshHands();
            _connected = true;
            _initFailureLogged = false;
            _frameClock.Restart();
            _frame.Start();
            Log.Info("SteamVR に接続しました (手首ボタンを表示します)");
        }
        catch (Exception ex)
        {
            // openvr_api.dll が読めない、SteamVR 側の不調など。着替えそのものには影響させない
            Log.Warn("SteamVR に接続できませんでした (VR の手首ボタンは使えません)", ex);
            Disconnect(null);
        }
    }

    private void Disconnect(string? reason)
    {
        _frame.Stop();
        var wasConnected = _connected;
        _connected = false;
        _panelOpen = false;
        _buttonShown = false;
        _buttonAlpha = 0;
        _placement = null;
        _sentAlpha = -1;
        _touching.Clear();
        _stickAxis.Clear();
        try
        {
            if (_initialized && _button != 0) OpenVR.Overlay.DestroyOverlay(_button);
            if (_initialized && _panelHandle != 0) OpenVR.Overlay.DestroyOverlay(_panelHandle);
        }
        catch (Exception ex) { Log.Debug("SteamVR のオーバーレイを片付けられませんでした", ex); }
        _button = 0;
        _panelHandle = 0;
        if (!_initialized) return;
        _initialized = false;
        try { OpenVR.Shutdown(); } catch (Exception ex) { Log.Debug("SteamVR から切断できませんでした", ex); }
        if (wasConnected && reason is not null) Log.Info($"SteamVR から切断しました ({reason})");
    }

    private static void Check(EVROverlayError err, string what)
    {
        if (err != EVROverlayError.None) throw new InvalidOperationException($"{what}に失敗しました ({err})");
    }

    /// <summary>SteamVR のレーザーで指して押せるようにする (ダッシュボードを開いていなくても)。</summary>
    private static void SetupMouse(ulong handle, int width, int height)
    {
        var overlay = OpenVR.Overlay;
        overlay.SetOverlayInputMethod(handle, VROverlayInputMethod.Mouse);
        var scale = new HmdVector2_t { v0 = width, v1 = height };
        overlay.SetOverlayMouseScale(handle, ref scale);
        overlay.SetOverlayFlag(handle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, true);
    }

    /// <summary>
    /// 画像を SteamVR に渡す。生データで渡せなかったとき (大きさの上限など) は、一時ファイル経由に切り替える。
    /// </summary>
    /// <param name="rgba">RGBA の画素 (上の行から)</param>
    /// <param name="fallback">一時ファイル経由に切り替えるときに使う画像 (その時だけ作る)</param>
    private void Upload(ulong handle, byte[] rgba, int width, int height, Func<BitmapSource> fallback)
    {
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        EVROverlayError err;
        try { err = OpenVR.Overlay.SetOverlayRaw(handle, pin.AddrOfPinnedObject(), (uint)width, (uint)height, 4); }
        finally { pin.Free(); }
        if (err == EVROverlayError.None) return;
        var bmp = fallback();

        // 同じパスだと読み直されないことがあるので、2 つの名前を交互に使う
        var path = Path.Combine(Path.GetTempPath(), $"VRCAvatarChanger-vr-{handle}-{_fileFlip ^= 1}.png");
        using (var stream = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(stream);
        }
        var fileErr = OpenVR.Overlay.SetOverlayFromFile(handle, path);
        if (fileErr != EVROverlayError.None) Log.Warn($"SteamVR に画像を渡せませんでした (raw: {err} / file: {fileErr})");
    }

    private void UploadButton(bool panelOpen)
        => Upload(_button, VrPanel.ButtonRgba(panelOpen), VrPanel.ButtonSize, VrPanel.ButtonSize, () => VrPanel.RenderButton(panelOpen));

    // ---------------- 毎フレーム ----------------

    private void Frame()
    {
        if (!_connected) return;
        try
        {
            var system = OpenVR.System;
            var overlay = OpenVR.Overlay;
            var ev = new VREvent_t();
            while (system.PollNextEvent(ref ev, EventSize))
            {
                // コントローラーの付け替え・持ち替えで番号の指す機器が変わるので、左右の手と軸を調べ直す
                if ((EVREventType)ev.eventType is EVREventType.VREvent_TrackedDeviceActivated
                    or EVREventType.VREvent_TrackedDeviceDeactivated or EVREventType.VREvent_TrackedDeviceRoleChanged)
                {
                    RefreshHands();
                    _stickAxis.Clear();
                }
                if ((EVREventType)ev.eventType != EVREventType.VREvent_Quit) continue;
                // SteamVR が終了しようとしている。すぐに手を離さないと SteamVR 側で待たされる
                system.AcknowledgeQuit_Exiting();
                Disconnect("SteamVR が終了するため");
                return;
            }
            while (overlay.PollNextOverlayEvent(_button, ref ev, EventSize))
                if ((EVREventType)ev.eventType == EVREventType.VREvent_MouseButtonUp) Press(TogglePanel);
            while (overlay.PollNextOverlayEvent(_panelHandle, ref ev, EventSize))
                HandlePanelEvent(ev);

            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _poses);
            var dt = (float)Math.Min(_frameClock.Elapsed.TotalSeconds, 0.1); // 止まっていた後に一気に動かないよう上限
            _frameClock.Restart();
            UpdateButton();
            if (_panelOpen)
            {
                MovePanelByStick(dt);
                PokePanel();
                RefreshPanel();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("SteamVR とのやりとりで問題が起きたため切断しました (次の周期で繋ぎ直します)", ex);
            Disconnect(null);
        }
    }

    /// <summary>連打・触れたままの揺れで二重に反応しないよう、間を空けて 1 回だけ実行する。</summary>
    private void Press(Action action)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPress < PressCooldown) return;
        _lastPress = now;
        action();
    }

    // ---------------- 手首ボタン ----------------

    private bool RightHanded => _settings.VrHand == "right";

    /// <summary>コントローラーから見た手首ボタンの位置と向き。</summary>
    private Matrix4x4 ButtonOffset()
    {
        var side = RightHanded ? -1f : 1f; // 手の甲 (腕の外側) は、左手なら -X、右手なら +X
        var roll = side * WatchRollDeg * MathF.PI / 180;
        // 面の表 (+Z) を甲の向きへ。画像の上 (+Y) は手の側 (-Z) を向け、腕時計と同じ向きで読めるようにする
        var rotation = Matrix4x4.CreateRotationX(-MathF.PI / 2) * Matrix4x4.CreateRotationZ(roll);
        var normal = Vector3.TransformNormal(Vector3.UnitZ, rotation);
        var along = WatchBackM + (float)_settings.VrButtonAlongCm / 100;
        var lift = WatchLiftM + (float)_settings.VrButtonHeightCm / 100;
        return rotation * Matrix4x4.CreateTranslation(new Vector3(0, 0, along) + normal * lift);
    }

    /// <summary>左右のコントローラーの機器番号を調べ直す。接続時と、機器の接続・持ち替えのときだけ呼ぶ。</summary>
    private void RefreshHands()
    {
        var system = OpenVR.System;
        _leftHand = system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
        _rightHand = system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
    }

    private uint HandDevice(ETrackedControllerRole role) => role == ETrackedControllerRole.RightHand ? _rightHand : _leftHand;

    private void UpdateButton()
    {
        var hand = RightHanded ? _rightHand : _leftHand;
        var other = RightHanded ? _leftHand : _rightHand;
        var hmd = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!_vrchatRunning || !IsValid(hand) || !hmd.bPoseIsValid)
        {
            FadeButton(false);
            return;
        }

        // 付ける手・ずらし量が変わったときだけ、位置を計算し直して貼り直す (普段は SteamVR が手に追従させる)
        var placement = (hand, _settings.VrHand, _settings.VrButtonAlongCm, _settings.VrButtonHeightCm);
        if (placement != _placement)
        {
            _buttonOffset = ButtonOffset();
            var m = ToHmd(_buttonOffset);
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_button, hand, ref m);
            _placement = placement;
        }

        // 手首を顔に向けて、そちらを見ているときだけ出す (腕時計を見る動作)
        var world = _buttonOffset * ToNumerics(_poses[hand].mDeviceToAbsoluteTracking);
        var head = ToNumerics(hmd.mDeviceToAbsoluteTracking);
        var center = world.Translation;
        var toEye = head.Translation - center;
        var distance = toEye.Length();
        var facing = Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, world)), toEye / distance);
        var looking = Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, head)), -toEye / distance);
        var visible = distance < 0.9f && facing > 0.4f && looking > 0.7f;
        FadeButton(visible);

        // 触れているかは見えていない間も追っておく。見えた瞬間に「もう触れていた」手を、押したとは数えないため
        if (IsValid(other) && Matrix4x4.Invert(world, out var inverse)
            && Poke(_button, inverse, ButtonWidthM, VrPanel.ButtonSize, VrPanel.ButtonSize, other, out _) && visible)
            Press(TogglePanel);
    }

    private void FadeButton(bool visible)
    {
        var overlay = OpenVR.Overlay;
        _buttonAlpha += ((visible ? 1f : 0f) - _buttonAlpha) * 0.25f;
        if (_buttonAlpha > 0.97f) _buttonAlpha = 1; // 近づき切ったら止める (透明度の送り直しを止めるため)
        if (_buttonAlpha < 0.03f)
        {
            _buttonAlpha = 0;
            if (_buttonShown) { overlay.HideOverlay(_button); _buttonShown = false; }
            return;
        }
        if (_buttonAlpha != _sentAlpha)
        {
            overlay.SetOverlayAlpha(_button, _buttonAlpha);
            _sentAlpha = _buttonAlpha;
        }
        if (!_buttonShown) { overlay.ShowOverlay(_button); _buttonShown = true; }
    }

    private bool IsValid(uint device)
        => device != OpenVR.k_unTrackedDeviceIndexInvalid && device < _poses.Length && _poses[device].bPoseIsValid;

    /// <summary>
    /// コントローラーの先で面に触れた瞬間か。触れた場所は画像の座標 (px、左上が原点) で返す。
    /// 触れたままの間は 1 回しか反応しない。
    /// </summary>
    /// <param name="overlayInverse">面の姿勢の逆行列 (ワールド → 面の座標)</param>
    private bool Poke(ulong overlay, Matrix4x4 overlayInverse, float widthM, int texWidth, int texHeight, uint device, out System.Windows.Point at)
    {
        var controller = ToNumerics(_poses[device].mDeviceToAbsoluteTracking);
        var tip = Vector3.Transform(new Vector3(0, 0, -PokeTipM), controller);
        var inside = TouchLocal(overlayInverse, widthM, texWidth, texHeight, tip, out at);
        var key = (overlay, device);
        var was = _touching.GetValueOrDefault(key);
        _touching[key] = inside;
        return inside && !was;
    }

    /// <summary>点 (ワールド座標) が面に触れているか。触れていれば、その場所を画像の座標 (px、左上が原点) で返す。</summary>
    internal static bool TouchPoint(Matrix4x4 overlayWorld, float widthM, int texWidth, int texHeight, Vector3 point, out System.Windows.Point at)
    {
        at = default;
        return Matrix4x4.Invert(overlayWorld, out var inverse) && TouchLocal(inverse, widthM, texWidth, texHeight, point, out at);
    }

    /// <summary><see cref="TouchPoint"/> の、逆行列を計算済みの版 (毎フレーム呼ぶ側は置き直したときだけ計算する)。</summary>
    private static bool TouchLocal(Matrix4x4 overlayInverse, float widthM, int texWidth, int texHeight, Vector3 point, out System.Windows.Point at)
    {
        at = default;
        // 面の座標: 中心が原点、+X が右、+Y が上、+Z が表側
        var local = Vector3.Transform(point, overlayInverse);
        var heightM = widthM * texHeight / texWidth;
        if (MathF.Abs(local.Z) >= PokeDepthM || MathF.Abs(local.X) > widthM / 2 || MathF.Abs(local.Y) > heightM / 2) return false;
        at = new System.Windows.Point((local.X / widthM + 0.5) * texWidth, (0.5 - local.Y / heightM) * texHeight);
        return true;
    }

    // ---------------- パネル ----------------

    private void TogglePanel()
    {
        if (_panelOpen) ClosePanel();
        else OpenPanel();
    }

    private void OpenPanel()
    {
        var head = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!head.bPoseIsValid) return;
        if (!_busy && _loadingTab is null) SetStatus("");
        Reload(resetPage: true); // 閉じている間に PC 側で一覧やグループが変わっていることがあるので、開くたびに作り直す

        // 顔の正面 (水平方向) に、少し下げて置く
        var headWorld = ToNumerics(head.mDeviceToAbsoluteTracking);
        var forward = Vector3.TransformNormal(-Vector3.UnitZ, headWorld);
        forward.Y = 0;
        _panelForward = forward.LengthSquared() < 1e-4f ? -Vector3.UnitZ : Vector3.Normalize(forward);
        _panelAnchor = headWorld.Translation;
        PlacePanel();

        RenderPanel();
        OpenVR.Overlay.ShowOverlay(_panelHandle);
        _panelOpen = true;
        UploadButton(panelOpen: true);
    }

    /// <summary>開いたときの頭の位置と正面から、今の距離でパネルを置く。</summary>
    private void PlacePanel()
    {
        _panelWorld = PanelPose(_panelAnchor, _panelForward, _panelDistance);
        Matrix4x4.Invert(_panelWorld, out _panelInverse);
        var m = ToHmd(_panelWorld);
        OpenVR.Overlay.SetOverlayTransformAbsolute(_panelHandle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
    }

    /// <summary>頭の位置 anchor から正面 forward (水平・単位ベクトル) へ distance 離し、少し下げてこちらへ向けた姿勢。</summary>
    internal static Matrix4x4 PanelPose(Vector3 anchor, Vector3 forward, float distance)
    {
        var position = anchor + forward * distance - Vector3.UnitY * PanelDropM;
        var yaw = MathF.Atan2(-forward.X, -forward.Z); // 面の表 (+Z) をこちらへ向ける
        return Matrix4x4.CreateRotationX(-PanelTiltDeg * MathF.PI / 180)
               * Matrix4x4.CreateRotationY(yaw)
               * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// グリップを握ったままスティックを前後に倒すと、パネルを奥 / 手前へ動かす (前に倒すと奥)。
    /// 左右どちらの手でもよい。両手で倒したときは大きく倒しているほうを使う。
    /// </summary>
    private void MovePanelByStick(float dt)
    {
        var system = OpenVR.System;
        var tilt = 0f;
        foreach (var role in Hands)
        {
            var device = HandDevice(role);
            if (!IsValid(device)) continue;
            var state = new VRControllerState_t();
            if (!system.GetControllerState(device, ref state, ControllerStateSize)) continue;
            if ((state.ulButtonPressed & (1UL << (int)EVRButtonId.k_EButton_Grip)) == 0) continue;
            var y = StickY(device, state);
            if (MathF.Abs(y) > MathF.Abs(tilt)) tilt = y;
        }
        var next = NextPanelDistance(_panelDistance, tilt, dt);
        if (next == _panelDistance) return;
        _panelDistance = next;
        PlacePanel();
    }

    private static readonly uint ControllerStateSize = (uint)Marshal.SizeOf<VRControllerState_t>();

    /// <summary>スティックの傾き (-1〜1) と経過時間から、次の距離を決める。遊びを除いてなめらかに効かせ、上限・下限で止める。</summary>
    internal static float NextPanelDistance(float distance, float tilt, float dt)
    {
        if (MathF.Abs(tilt) < StickDeadZone) return distance;
        // 遊びの端から 0 で立ち上がるようにする (倒し始めで急に動かない)
        var amount = (MathF.Abs(tilt) - StickDeadZone) / (1 - StickDeadZone) * MathF.Sign(tilt);
        return Math.Clamp(distance + amount * PanelMoveSpeed * dt, MinPanelDistanceM, MaxPanelDistanceM);
    }

    /// <summary>
    /// スティックの前後の傾き (前が +)。軸の割り当ては機種で違う (Index は 3 番、Touch は 0 番など) ので、
    /// 機器に「どの軸がスティックか」を聞いて使う。スティックの無い機種はトラックパッドで代用する。
    /// </summary>
    private float StickY(uint device, VRControllerState_t state)
    {
        if (!_stickAxis.TryGetValue(device, out var axis))
        {
            axis = -1;
            var pad = -1;
            for (var i = 0; i < 5; i++)
            {
                var err = ETrackedPropertyError.TrackedProp_Success;
                var type = (EVRControllerAxisType)OpenVR.System.GetInt32TrackedDeviceProperty(
                    device, ETrackedDeviceProperty.Prop_Axis0Type_Int32 + i, ref err);
                if (err != ETrackedPropertyError.TrackedProp_Success) continue;
                if (type == EVRControllerAxisType.k_eControllerAxis_Joystick) { axis = i; break; }
                if (type == EVRControllerAxisType.k_eControllerAxis_TrackPad && pad < 0) pad = i;
            }
            if (axis < 0) axis = pad;
            _stickAxis[device] = axis;
        }
        return axis switch
        {
            0 => state.rAxis0.y,
            1 => state.rAxis1.y,
            2 => state.rAxis2.y,
            3 => state.rAxis3.y,
            4 => state.rAxis4.y,
            _ => 0,
        };
    }

    private void ClosePanel()
    {
        if (!_connected) return;
        OpenVR.Overlay.HideOverlay(_panelHandle);
        _panelOpen = false;
        UploadButton(panelOpen: false);
    }

    private VrView View => new(_settings.VrTab, _settings.VrSortKey, _settings.VrGroupView, _filter, _openGroup);

    /// <summary>
    /// 今の見せ方で、並べるタイルを作り直す。
    /// resetPage なら今着ているアバターのページから (タブや並びを変えたとき)、そうでなければ今のページのまま。
    /// タブの一覧がまだ手元に無ければ、VRChat へ取りに行き、届いたら作り直す。
    /// </summary>
    private void Reload(bool resetPage)
    {
        if (_openGroup is not null && !_source.GroupExists(_openGroup)) _openGroup = null;
        var tab = _settings.VrTab;
        _panel.Tab = tab;
        _panel.SortKey = _settings.VrSortKey;
        _panel.GroupView = _settings.VrGroupView;
        _panel.OpenGroupName = _openGroup?.Name;

        var items = _source.ItemsOf(tab);
        if (items is null)
        {
            _filter = null;
            _panel.Filter = null;
            _panel.SetChips([]);
            _panel.SetItems([], resetPage: true);
            LoadTabAsync(tab).Forget();
        }
        else
        {
            var chips = _source.FilterValuesOf(items, tab);
            if (_filter is not null && !chips.Contains(_filter)) _filter = null; // 候補から消えた (タグを外したなど)
            _panel.Filter = _filter;
            _panel.SetChips(chips);
            _panel.SetItems(_source.Arrange(items, View), resetPage);
        }
        RequestPageThumbnails();
        _panelDirty = true;
    }

    /// <summary>まだ手元に無いタブの一覧を取りに行く。同じタブを重ねて取りには行かない。</summary>
    private async Task LoadTabAsync(string tab)
    {
        if (_loadingTab == tab) return;
        _loadingTab = tab;
        SetStatus("一覧を読み込んでいます...");
        try
        {
            await _source.LoadAsync(tab);
            if (!_connected || !_panelOpen || _settings.VrTab != tab) return; // 待っている間に閉じた / タブを移った
            SetStatus("");
            Reload(resetPage: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"VR パネル: 一覧 ({tab}) を読み込めませんでした", ex);
            if (_connected && _panelOpen && _settings.VrTab == tab) SetStatus("一覧を読み込めませんでした: " + FriendlyError.Of(ex), error: true);
        }
        finally { _loadingTab = null; }
    }

    private void SetStatus(string text, bool error = false)
    {
        _panel.Status = text;
        _panel.StatusIsError = error;
        _panelDirty = true;
    }

    private void RequestPageThumbnails()
    {
        for (var slot = 0; slot < _panel.PageItemCount; slot++) _source.RequestThumbnail(_panel.PageItem(slot));
    }

    private void HandlePanelEvent(VREvent_t ev)
    {
        switch ((EVREventType)ev.eventType)
        {
            case EVREventType.VREvent_MouseMove:
                // SteamVR のマウス座標は下が原点なので、画像の座標 (上が原点) に直す
                var hover = _panel.HitTest(ev.data.mouse.x, VrPanel.Height - ev.data.mouse.y);
                if (hover != _panel.Hover) { _panel.Hover = hover; _panelDirty = true; }
                break;
            case EVREventType.VREvent_FocusLeave:
                if (_panel.Hover != default) { _panel.Hover = default; _panelDirty = true; }
                break;
            case EVREventType.VREvent_MouseButtonUp when ev.data.mouse.button == (uint)EVRMouseButton.Left:
                var hit = _panel.HitTest(ev.data.mouse.x, VrPanel.Height - ev.data.mouse.y);
                Press(() => Click(hit));
                break;
            case EVREventType.VREvent_ScrollDiscrete:
                // スティックを上に倒す (ydelta > 0) と前のページ
                if (ev.data.scroll.ydelta != 0) Click(new VrPanel.Hit(ev.data.scroll.ydelta > 0 ? VrPanel.HitKind.Prev : VrPanel.HitKind.Next));
                break;
        }
    }

    private void PokePanel()
    {
        foreach (var role in Hands)
        {
            var device = HandDevice(role);
            if (IsValid(device) && Poke(_panelHandle, _panelInverse, PanelWidthM, VrPanel.Width, VrPanel.Height, device, out var at))
            {
                var hit = _panel.HitTest(at.X, at.Y);
                Press(() => Click(hit));
            }
        }
    }

    private void Click(VrPanel.Hit hit)
    {
        switch (hit.Kind)
        {
            case VrPanel.HitKind.Close:
                ClosePanel();
                return;
            case VrPanel.HitKind.Tab when hit.Index >= 0 && hit.Index < VrPanel.Tabs.Length:
                if (_settings.VrTab == VrPanel.Tabs[hit.Index]) return;
                _settings.VrTab = VrPanel.Tabs[hit.Index];
                _filter = null;     // 絞り込みの候補はタブごとに違う
                _openGroup = null;
                if (_loadingTab is null && !_busy) SetStatus("");
                _source.SaveSettings();
                Reload(resetPage: true);
                break;
            case VrPanel.HitKind.SortRecent:
            case VrPanel.HitKind.SortName:
                var key = hit.Kind == VrPanel.HitKind.SortRecent ? "recent_asc" : "name_asc";
                if (_settings.VrSortKey == key) return;
                _settings.VrSortKey = key;
                _source.SaveSettings();
                Reload(resetPage: true);
                break;
            case VrPanel.HitKind.GroupToggle:
                _settings.VrGroupView = !_settings.VrGroupView;
                _openGroup = null;
                _source.SaveSettings();
                Reload(resetPage: true);
                break;
            case VrPanel.HitKind.Chip:
                _filter = hit.Index <= 0 || hit.Index > _panel.Chips.Count ? null : _panel.Chips[hit.Index - 1];
                Reload(resetPage: true);
                break;
            case VrPanel.HitKind.ChipPrev:
            case VrPanel.HitKind.ChipNext:
                _panel.ScrollChips(hit.Kind == VrPanel.HitKind.ChipPrev ? -1 : 1);
                break;
            case VrPanel.HitKind.Back:
                var group = _openGroup;
                _openGroup = null;
                Reload(resetPage: false);
                _panel.ShowPageOf(t => t.Group == group); // 開いていたグループのタイルが見えるところへ
                RequestPageThumbnails();
                break;
            case VrPanel.HitKind.Prev:
            case VrPanel.HitKind.Next:
                _panel.TurnPage(hit.Kind == VrPanel.HitKind.Prev ? -1 : 1);
                RequestPageThumbnails();
                break;
            case VrPanel.HitKind.Tile when hit.Index >= 0 && hit.Index < _panel.Items.Count:
                var tile = _panel.Items[hit.Index];
                if (tile.Group is { } g)
                {
                    // グループは開いて中身を並べる (着替えるのは中のアバターを選んでから)
                    _openGroup = g;
                    Reload(resetPage: true);
                }
                else ChangeAsync(tile).Forget();
                break;
            default:
                return;
        }
        _panelDirty = true;
    }

    private async Task ChangeAsync(AvatarItem item)
    {
        if (_busy) return;
        _busy = true;
        SetStatus($"{item.Name} に着替えています...");
        bool ok;
        try { ok = await _source.ChangeAsync(item); }
        finally { _busy = false; }
        if (!_connected || !_panelOpen) return;
        SetStatus(ok ? $"{item.Name} に着替えました" : $"{item.Name} に着替えられませんでした", error: !ok);
        // 「今着ている」の印を付け直す (グループのタイルにも)。ページはそのまま
        if (ok) Reload(resetPage: false);
        // 着替えても閉じない。見比べながら続けて着替えられるよう、閉じるのは利用者の操作 (閉じるボタン / 手首ボタン) だけ
    }

    /// <summary>
    /// 表示が変わったとき (ホバー・ページ・サムネイルの到着など) だけ描き直して渡す。毎フレーム呼ばれるので、
    /// 変わっていないかの確認はメモリを確保せずに行い、続けて起きる変化は間隔を空けてまとめる。
    /// </summary>
    private void RefreshPanel()
    {
        var interval = _panelDirty ? HoverRenderInterval : ThumbnailsChanged() ? ThumbRenderInterval : (TimeSpan?)null;
        if (interval is null || DateTime.UtcNow - _lastRender < interval) return;
        RenderPanel();
    }

    /// <summary>描いたあとに、今のページのサムネイルが届いた (差し替わった) か。</summary>
    private bool ThumbnailsChanged()
    {
        for (var slot = 0; slot < VrPanel.PageSize; slot++)
        {
            var now = slot < _panel.PageItemCount ? _panel.PageItem(slot).Thumbnail : null;
            if (!ReferenceEquals(now, _renderedThumbs[slot])) return true;
        }
        return false;
    }

    private void RenderPanel()
    {
        Upload(_panelHandle, _panel.RenderRgba(), VrPanel.Width, VrPanel.Height, () => _panel.Render());
        for (var slot = 0; slot < VrPanel.PageSize; slot++)
            _renderedThumbs[slot] = slot < _panel.PageItemCount ? _panel.PageItem(slot).Thumbnail : null;
        _panelDirty = false;
        _lastRender = DateTime.UtcNow;
    }

    // ---------------- 行列 ----------------
    // SteamVR の 3x4 行列は列ベクトル流儀 (p' = M·p)、System.Numerics は行ベクトル流儀 (p' = p·M)。転置して行き来する。
    // Numerics で「A をしてから B」は A * B と書く。

    private static Matrix4x4 ToNumerics(HmdMatrix34_t m) => new(
        m.m0, m.m4, m.m8, 0,
        m.m1, m.m5, m.m9, 0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1);

    private static HmdMatrix34_t ToHmd(Matrix4x4 m) => new()
    {
        m0 = m.M11, m1 = m.M21, m2 = m.M31, m3 = m.M41,
        m4 = m.M12, m5 = m.M22, m6 = m.M32, m7 = m.M42,
        m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = m.M43,
    };

#if DEBUG
    /// <summary>検証用: SteamVR 無しで描画と当たり判定を確かめるため、パネルを外から触れるようにする。</summary>
    internal VrPanel PanelForTest => _panel;
    /// <summary>検証用: パネルを押したのと同じ処理を行う (画像の座標で)。</summary>
    internal void ClickForTest(double x, double y) => Click(_panel.HitTest(x, y));
    internal void ReloadForTest() => Reload(resetPage: true);
    internal void SetOpenForTest(bool open) { _panelOpen = open; _connected = open; }
    /// <summary>検証用: パネル 1 回ぶんの描き直し (SteamVR に渡す直前まで) を行い、渡すバイト数を返す。</summary>
    internal int RenderPanelBytesForTest() => _panel.RenderRgba().Length;
    internal static Matrix4x4 ButtonOffsetForTest(Settings s) => new VrOverlay(s, null!).ButtonOffset();
    internal static HmdMatrix34_t ToHmdForTest(Matrix4x4 m) => ToHmd(m);
    internal static Matrix4x4 ToNumericsForTest(HmdMatrix34_t m) => ToNumerics(m);
    internal const float PanelWidthForTest = PanelWidthM;
#endif
}
