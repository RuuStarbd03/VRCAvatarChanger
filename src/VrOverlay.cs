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
/// VR (SteamVR) での着替え。PC の Shift+1 の代わりに、手の甲のボタンからアバター選択パネルを開く。
///
/// ・SteamVR と VRChat の両方が動いているときだけ SteamVR に繋ぐ (こちらから SteamVR を起動することはない)。
/// ・ボタン (コード上は「手首ボタン」と呼ぶ) はコントローラーに貼り付けて手の甲に置き、手の甲を見たときだけ現れる
///   (普段は視界の邪魔をしない)。OVR Toolkit などの腕時計 (手首) とは重ならない。
///   反対の手で指してグリップを握るとつかんで動かせ、離した場所 (付けた手から見た位置と向き) を設定に残す。
/// ・ボタンもパネルも、指して (レイ) トリガーで押す。
///   レイは SteamVR のレーザー (マウス入力) を使わず自前で計算する (VrOverlay.Ray.cs)。SteamVR のレーザーを使うと、
///   指した瞬間に SteamVR がコントローラーの入力を奪い、VRChat に届かなくなる (プッシュトゥトークが切れていた)。
/// ・ボタンもパネルも、左右どちらの手でも指して押せる。
/// ・パネルは開いた時点の顔の正面に置き、その場に固定する (手を動かしても追いかけてこない)。
///   指してグリップを握るとつかんで好きな所へ動かせる (面はいつも顔を向く)。
/// ・アバターのタイルは 2 回押しでチェンジする (1 回目は選ぶだけ。PC のダブルクリックと同じ)。
/// ・PC のクイック着替えとは別物。タブ・並び順・グループ化・絞り込みは VR パネルの中で切り替え、
///   PC の画面を触らずに済むようにする (選んだタブ・並び順・グループ化は設定に残す)。
///
/// 見た目と当たり判定は VrPanel が受け持つ。ここは SteamVR とのやりとりと、手・頭の位置の計算だけ。
/// SteamVR の API は UI スレッドのタイマーからだけ呼ぶ (スレッドをまたがない)。
/// </summary>
internal sealed partial class VrOverlay : IDisposable
{
    private const string ButtonKey = "vrcavatarchanger.wrist";
    private const string PanelKey = "vrcavatarchanger.panel";
    private const float ButtonWidthM = 0.035f;     // ボタンの見た目の幅。手の甲から少しはみ出すくらい (小さすぎると指しにくい)
    private const float ButtonMaxAlpha = 0.85f;    // 一番濃いときでも少し透かす (向こうが見えて主張しすぎない)
    private const float PanelWidthM = 0.5f;

    // ---- 手首ボタンの置き場所 (コントローラー基準、m) ----
    // コントローラーの座標は -Z が指す向き、+Z が手首・肘の側、+Y が上 (親指側)、+X が右。
    // 手の甲の真ん中のおおよその位置の推測値。コントローラーの種類で原点が違うので、合わなければ設定で詰める。
    private const float HandBackM = 0.06f;     // コントローラーの原点から手の甲の真ん中まで (+Z)
    private const float HandLiftM = 0.045f;    // 握った手の中心から甲の表面まで
    private const float HandRollDeg = 70;      // 甲の向き: 真上 (0°) から腕の外側へ倒す角度。握ったとき手の甲は外を向く

    private static readonly TimeSpan PressCooldown = TimeSpan.FromMilliseconds(400);
    private const float MaxButtonReachM = 0.3f;    // つかんで動かせるのは、付けた手からこの距離まで (見失わないように)

    // ---- パネルの置き場所 (開いた時点の頭の位置から) ----
    private const float DefaultPanelDistanceM = 0.55f;
    // つかんで動かしたあとに次に開くときの距離 (顔から)。近すぎ・遠すぎで見失わないよう上限を設ける
    public const float MinPanelDistanceM = 0.3f;
    public const float MaxPanelDistanceM = 1.5f;
    private const float PanelMoveSpeed = 0.8f;    // つかんだままスティックをいっぱいに倒したときの、遠ざける / 近づける速さ (m/秒)
    private const float StickDeadZone = 0.25f;    // これより小さい傾きは無視 (スティックの戻りきらない揺れ)
    private const float PanelDropM = 0.15f;    // 目線より少し下
    private const float PanelTiltDeg = 15;     // 下に置くぶん、上を向けて顔に向ける

    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();

    // パネルの描き直しは 1 回で 2MB を SteamVR に渡すので、続けて起きる変化はまとめる
    // (GPU で渡せるときは、スクロールがなめらかに見えるよう 60 回/秒まで。生データで渡すときは 30 回/秒まで)
    private static readonly TimeSpan GpuRenderInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RawRenderInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan ThumbRenderInterval = TimeSpan.FromMilliseconds(150); // サムネイルが 1 枚ずつ届くとき
    private const double ScrollFriction = 4;   // 離したあとに流れるスクロールの減り方 (1 秒で e^-4 ≒ 2% まで)
    // パネルの距離を変えている間と、止めてからこの間は描き直さない。
    // 動かすとレイの当たる場所がずれてホバーが次々に切り替わり、そのたびに画像を差し替えるとちらつくため
    private static readonly TimeSpan MoveSettle = TimeSpan.FromMilliseconds(250);
    // パネルは 2 枚重ねで持ち、見えていない方に新しい画像を渡してから入れ替える。
    // 見えている 1 枚に SetOverlayRaw で渡し直すと、差し替えの途中で一瞬何も映らないことがあり、ホバーのたびにちらついていた。
    // 新しい方を手前に出してから、この間を置いて古い方を隠す (SteamVR 側で画像が使えるようになるまでの余裕)
    private static readonly TimeSpan PanelSwapOverlap = TimeSpan.FromMilliseconds(50);

    private static readonly ETrackedControllerRole[] Hands = [ETrackedControllerRole.LeftHand, ETrackedControllerRole.RightHand];

    private readonly Settings _settings;
    private readonly IVrAvatarSource _source;

    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _frame = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly VrPanel _panel = new();
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

    private bool _initialized; // OpenVR.Init が通った (Shutdown が要る)
    private bool _connected;   // オーバーレイまで作り終えた
    private bool _checking;
    private bool _vrchatRunning;
    private bool _initFailureLogged;
    private ulong _button;
    private readonly ulong[] _panels = new ulong[2]; // 2 枚重ねのパネル。_panels[_front] が今見せている方
    private int _front;
    private bool _hidePending;      // 入れ替えたあと、古い方 (_panels[_front ^ 1]) をまだ隠していない
    private DateTime _swappedAt;
    private VrGpu? _gpu;            // パネルを GPU で渡す (作れなかった / 渡せなかったら null で、生データ + 2 枚の入れ替えに戻す)
    private double _scrollVelocity; // ドラッグを離したあとに流れているスクロールの速さ (px/秒)
    private (int Start, int Count) _requestedThumbs = (-1, 0); // サムネイルを最後に頼んだ範囲 (同じ範囲を毎フレーム頼まない)
    private (uint Device, string Hand, float[]? Pose)? _placement; // 手首ボタンを最後に貼り付けた条件。変わったら貼り直す
    private Matrix4x4 _buttonOffset;  // その条件での、コントローラーから見た手首ボタンの位置
    private uint _leftHand = OpenVR.k_unTrackedDeviceIndexInvalid;   // 左右のコントローラーの機器番号。
    private uint _rightHand = OpenVR.k_unTrackedDeviceIndexInvalid;  // 毎フレーム問い合わせず、接続・持ち替えのときだけ調べ直す
    private float _buttonAlpha;
    private float _sentAlpha = -1;    // 最後に SteamVR へ送った透明度 (同じ値を送り直さない)
    private bool _buttonShown;
    private bool _panelOpen;
    private Matrix4x4 _panelWorld;
    private Matrix4x4 _panelInverse;       // レイをパネルの座標に直すための逆行列 (置き直したときだけ計算する)
    private float _panelDistance = DefaultPanelDistanceM; // 変えた距離は、アプリを閉じるまで次に開いたときも使う
    private readonly Dictionary<uint, int> _stickAxis = [];  // コントローラーごとの、スティック (無ければトラックパッド) の軸番号
    private readonly Stopwatch _frameClock = new();
    private bool _panelDirty;
    private DateTime _lastRender;
    private readonly BitmapSource?[] _renderedThumbs = new BitmapSource?[VrPanel.MaxVisibleCount]; // 描いたときのサムネイル
    private DateTime _lastPress;
    private DateTime _lastPanelMove;        // 最後にパネルを動かした (スティック / つかんで) 時刻
    private readonly byte[] _uploadedPanel = new byte[VrPanel.Width * VrPanel.Height * 4]; // 最後に SteamVR へ渡したパネルの画像
    private bool _uploadedPanelValid;       // _uploadedPanel が今のオーバーレイに渡したものか (繋ぎ直したら無効)
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
        App.ThemeApplied += OnThemeApplied;
    }

    /// <summary>アクセント色が変わったら、手首ボタンとパネルを新しい色で描き直す。</summary>
    private void OnThemeApplied()
    {
        if (!_connected) return;
        UploadButton(_panelOpen);
        if (_panelOpen) _panelDirty = true;
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

    public void Dispose()
    {
        App.ThemeApplied -= OnThemeApplied;
        Stop();
    }

    // ---------------- 接続 / 切断 ----------------

    private async Task WatchAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            // プロセスの列挙は全プロセスを見るので UI スレッドの外で
            var (steamVr, vrchat) = await Task.Run(() => (Shell.IsRunning("vrserver"), Shell.IsRunning("VRChat")));
            if (!_watch.IsEnabled) return; // 確認している間に止められた
            _vrchatRunning = vrchat;
            if (steamVr && vrchat && !_connected) Connect();
            else if (_connected && !(steamVr && vrchat)) Disconnect(steamVr ? "VRChat が終了したため" : "SteamVR が終了したため");
        }
        catch (Exception ex) { Log.Debug("SteamVR / VRChat の起動を確認できませんでした (次の周期で再確認します)", ex); }
        finally { _checking = false; }
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
            // ボタンにもパネルにも SteamVR のレーザー (マウス入力) は付けない。指す操作は自前のレイで行う (VrOverlay.Ray.cs)
            UploadButton(panelOpen: false);

            for (var i = 0; i < _panels.Length; i++)
            {
                Check(overlay.CreateOverlay(i == 0 ? PanelKey : PanelKey + ".b", "AvatarChanger アバター選択", ref _panels[i]), "パネルの作成");
                overlay.SetOverlayWidthInMeters(_panels[i], PanelWidthM);
            }
            _front = 0;
            _hidePending = false;
            // パネルの画像は GPU で渡す。SteamVR が画面を出している GPU に合わせる (GPU が 2 つある PC で受け取れないことがあるため)
            var adapter = -1;
            OpenVR.System.GetDXGIOutputInfo(ref adapter);
            _gpu = VrGpu.Create(adapter);
            CreateCursor();

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
        _grabbing = false;
        _panelAppear.Reset();
        _panelClosing = false;
        _sentAlpha = -1;
        _stickAxis.Clear();
        _uploadedPanelValid = false; // オーバーレイを作り直すので、前に渡した画像はもう無い
        try
        {
            if (_initialized && _button != 0) OpenVR.Overlay.DestroyOverlay(_button);
            foreach (var panel in _panels)
                if (_initialized && panel != 0) OpenVR.Overlay.DestroyOverlay(panel);
            if (_initialized) DestroyCursors();
        }
        catch (Exception ex) { Log.Debug("SteamVR のオーバーレイを片付けられませんでした", ex); }
        _button = 0;
        Array.Clear(_panels);
        _hidePending = false;
        _gpu?.Dispose(); // オーバーレイを壊したあとなので、テクスチャを手放してよい
        _gpu = null;
        _scrollVelocity = 0;
        ResetRay();
        if (!_initialized) return;
        _initialized = false;
        try { OpenVR.Shutdown(); } catch (Exception ex) { Log.Debug("SteamVR から切断できませんでした", ex); }
        if (wasConnected && reason is not null) Log.Info($"SteamVR から切断しました ({reason})");
    }

    private static void Check(EVROverlayError err, string what)
    {
        if (err != EVROverlayError.None) throw new InvalidOperationException($"{what}失敗 ({err})");
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
            var ev = new VREvent_t();
            while (system.PollNextEvent(ref ev, EventSize))
            {
                // コントローラーの付け替え・持ち替えで番号の指す機器が変わるので、左右の手と軸を調べ直す
                if ((EVREventType)ev.eventType is EVREventType.VREvent_TrackedDeviceActivated
                    or EVREventType.VREvent_TrackedDeviceDeactivated or EVREventType.VREvent_TrackedDeviceRoleChanged)
                {
                    RefreshHands();
                    _stickAxis.Clear();
                    _tipOffset.Clear();
                }
                if ((EVREventType)ev.eventType != EVREventType.VREvent_Quit) continue;
                // SteamVR が終了しようとしている。すぐに手を離さないと SteamVR 側で待たされる
                system.AcknowledgeQuit_Exiting();
                Disconnect("SteamVR が終了するため");
                return;
            }

            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _poses);
            var dt = (float)Math.Min(_frameClock.Elapsed.TotalSeconds, 0.1); // 止まっていた後に一気に動かないよう上限
            _frameClock.Restart();
            UpdateButton();
            if (_panelOpen)
            {
                MoveGrabbedPanel(dt);
                ApplyScrollInertia(dt);
            }
            UpdateRay(dt);
            if (_panelOpen) RefreshPanel();
            AnimatePanelOverlay();
        }
        catch (Exception ex)
        {
            Log.Warn("SteamVR とのやりとりで問題が起きたため切断しました (次の周期で繋ぎ直します)", ex);
            Disconnect(null);
        }
    }

    /// <summary>連打で二重に反応しないよう、間を空けて 1 回だけ実行する。</summary>
    private void Press(Action action)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPress < PressCooldown) return;
        _lastPress = now;
        action();
    }

    // ---------------- 手首ボタン ----------------

    private bool RightHanded => _settings.VrHand == "right";

    /// <summary>コントローラーから見た手首ボタンの位置と向き。つかんで動かした位置があればそれ、なければ手の甲。</summary>
    private Matrix4x4 ButtonOffset()
        => PoseFromArray(_settings.VrButtonPose) is { } moved ? moved : HandBackOffset(RightHanded);

    /// <summary>手の甲の真ん中に、甲の向きへ面を向けて置く位置 (既定の位置)。</summary>
    internal static Matrix4x4 HandBackOffset(bool rightHanded)
    {
        var side = rightHanded ? -1f : 1f; // 手の甲 (腕の外側) は、左手なら -X、右手なら +X
        var roll = side * HandRollDeg * MathF.PI / 180;
        // 面の表 (+Z) を甲の向きへ。画像の上 (+Y) は手の側 (-Z) を向け、腕時計と同じ向きで読めるようにする
        var rotation = Matrix4x4.CreateRotationX(-MathF.PI / 2) * Matrix4x4.CreateRotationZ(roll);
        var normal = Vector3.TransformNormal(Vector3.UnitZ, rotation);
        return rotation * Matrix4x4.CreateTranslation(new Vector3(0, 0, HandBackM) + normal * HandLiftM);
    }

    /// <summary>設定に残す形 (3x4 の 12 個) と行列を行き来する。壊れた値や遠すぎる位置は null (既定の位置に戻す)。</summary>
    internal static float[] PoseToArray(Matrix4x4 m)
        => [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43];

    internal static Matrix4x4? PoseFromArray(float[]? a)
    {
        if (a is not { Length: 12 } || a.Any(v => !float.IsFinite(v))) return null;
        var m = new Matrix4x4(a[0], a[1], a[2], 0, a[3], a[4], a[5], 0, a[6], a[7], a[8], 0, a[9], a[10], a[11], 1);
        return m.Translation.Length() <= MaxButtonReachM + 0.01f ? m : null;
    }

    /// <summary>左右のコントローラーの機器番号を調べ直す。接続時と、機器の接続・持ち替えのときだけ呼ぶ。</summary>
    private void RefreshHands()
    {
        var system = OpenVR.System;
        _leftHand = system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
        _rightHand = system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
    }

    private uint HandDevice(ETrackedControllerRole role) => role == ETrackedControllerRole.RightHand ? _rightHand : _leftHand;

    // ---- つかんで動かす ----
    private bool _grabbing;
    private Matrix4x4 _grabHold;      // つかんだ手から見たボタンの位置 (つかんでいる間はこの関係のまま手についてくる)
    private bool _gripWasDown;        // 反対の手のグリップを前のフレームで握っていたか (握った瞬間だけつかむ)

    private void UpdateButton()
    {
        var hand = RightHanded ? _rightHand : _leftHand;
        var other = RightHanded ? _leftHand : _rightHand;
        var hmd = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        _buttonVisible = false;
        if (!_vrchatRunning || !IsValid(hand) || !hmd.bPoseIsValid)
        {
            _grabbing = false;
            FadeButton(false);
            return;
        }
        var handPose = ToNumerics(_poses[hand].mDeviceToAbsoluteTracking);
        var otherValid = IsValid(other);
        var otherPose = otherValid ? ToNumerics(_poses[other].mDeviceToAbsoluteTracking) : Matrix4x4.Identity;
        var gripping = otherValid && GripPressed(other);
        var gripPressedNow = gripping && !_gripWasDown;
        _gripWasDown = gripping;

        if (_grabbing)
        {
            // つかんでいる間: 反対の手についてくる。離したら、付けた手から見た位置として残す
            var relative = ClampReach(_grabHold * otherPose * Inverse(handPose));
            var m = ToHmd(relative);
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_button, hand, ref m);
            _buttonOffset = relative;
            _buttonWorld = relative * handPose;
            FadeButton(true);
            if (gripping && otherValid) return;
            _grabbing = false;
            _settings.VrButtonPose = PoseToArray(relative);
            _placement = (hand, _settings.VrHand, _settings.VrButtonPose);
            _source.SaveSettings();
            Log.Info("VR: ボタンの位置を動かしました");
            return;
        }

        // 付ける手・位置の設定が変わったときだけ、貼り直す (普段は SteamVR が手に追従させる)
        var placement = (hand, _settings.VrHand, _settings.VrButtonPose);
        if (placement != _placement)
        {
            _buttonOffset = ButtonOffset();
            var m = ToHmd(_buttonOffset);
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_button, hand, ref m);
            _placement = placement;
        }

        // 手の甲を顔に向けて、そちらを見ているときだけ出す (腕時計を見るのと同じ動作)
        var world = _buttonOffset * handPose;
        var head = ToNumerics(hmd.mDeviceToAbsoluteTracking);
        var center = world.Translation;
        var toEye = head.Translation - center;
        var distance = toEye.Length();
        var facing = Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, world)), toEye / distance);
        var looking = Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, head)), -toEye / distance);
        var visible = distance < 0.9f && facing > 0.4f && looking > 0.7f;
        FadeButton(visible);
        _buttonWorld = world;
        _buttonVisible = visible;

        // 反対の手で指したままグリップを握ったら、つかむ (押すのはトリガー: UpdateRay)
        if (visible && gripPressedNow && RayOnButtonBy(other))
        {
            _grabbing = true;
            _grabHold = world * Inverse(otherPose);
            Buzz(other);
        }
    }

    private static Matrix4x4 Inverse(Matrix4x4 m) => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;

    /// <summary>付けた手から遠くへ持っていかれたら、その向きのまま上限の距離まで戻す。</summary>
    internal static Matrix4x4 ClampReach(Matrix4x4 relative)
    {
        var t = relative.Translation;
        if (t.Length() > MaxButtonReachM) relative.Translation = Vector3.Normalize(t) * MaxButtonReachM;
        return relative;
    }

    private bool GripPressed(uint device)
    {
        var state = new VRControllerState_t();
        return OpenVR.System.GetControllerState(device, ref state, ControllerStateSize)
               && (state.ulButtonPressed & (1UL << (int)EVRButtonId.k_EButton_Grip)) != 0;
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
            overlay.SetOverlayAlpha(_button, _buttonAlpha * ButtonMaxAlpha);
            // 現れるときは小さめから広がる (透明度と一緒に変えるだけなので、描き直しは要らない)
            overlay.SetOverlayWidthInMeters(_button, ButtonWidthM * (Animations ? 0.8f + 0.2f * _buttonAlpha : 1f));
            _sentAlpha = _buttonAlpha;
        }
        if (!_buttonShown) { overlay.ShowOverlay(_button); _buttonShown = true; }
    }

    private bool IsValid(uint device)
        => device != OpenVR.k_unTrackedDeviceIndexInvalid && device < _poses.Length && _poses[device].bPoseIsValid;

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
        Reload(resetScroll: true); // 閉じている間に PC 側で一覧やグループが変わっていることがあるので、開くたびに作り直す

        // 顔の正面 (水平方向) に、少し下げて置く
        var headWorld = ToNumerics(head.mDeviceToAbsoluteTracking);
        var forward = Vector3.TransformNormal(-Vector3.UnitZ, headWorld);
        forward.Y = 0;
        forward = forward.LengthSquared() < 1e-4f ? -Vector3.UnitZ : Vector3.Normalize(forward);
        _panelWorld = PanelPose(headWorld.Translation, forward, _panelDistance);
        PlacePanel();

        RenderPanel(); // 閉じている間なので、入れ替えは待たずに済む
        // 閉じる動きの途中で開き直したときは、見せない方の 1 枚がまだ出ていることがあるので隠す
        OpenVR.Overlay.HideOverlay(_panels[_front ^ 1]);
        _hidePending = false;
        StartPanelAppear(closing: false);
        OpenVR.Overlay.ShowOverlay(_panels[_front]);
        _panelOpen = true;
        UploadButton(panelOpen: true);
    }

    // ---- 開く / 閉じるときの動き (パネル全体の透明度と大きさを変えるだけなので、描き直しは要らない) ----
    private const double PanelOpenMs = 180;
    private const double PanelCloseMs = 120;
    private readonly Stopwatch _panelAppear = new();
    private bool _panelClosing;

    private static bool Animations => System.Windows.SystemParameters.ClientAreaAnimation;

    private void StartPanelAppear(bool closing)
    {
        _panelClosing = closing;
        if (!Animations)
        {
            _panelAppear.Reset();
            SetPanelAppearance(1);
            return;
        }
        _panelAppear.Restart();
        SetPanelAppearance(closing ? 1 : 0);
    }

    /// <summary>開くときは透けた状態から少し大きくなりながら現れ、閉じるときは少し縮みながら消える。</summary>
    private void AnimatePanelOverlay()
    {
        if (!_panelAppear.IsRunning) return;
        var t = Math.Clamp(_panelAppear.Elapsed.TotalMilliseconds / (_panelClosing ? PanelCloseMs : PanelOpenMs), 0, 1);
        var shown = _panelClosing ? 1 - t * t : 1 - (1 - t) * (1 - t);
        SetPanelAppearance((float)shown);
        if (t < 1) return;
        _panelAppear.Reset();
        if (!_panelClosing) return;
        foreach (var panel in _panels) OpenVR.Overlay.HideOverlay(panel);
        _panelClosing = false;
        SetPanelAppearance(1); // 次に開くときに備えて戻しておく (開くときはまた 0 から)
    }

    private void SetPanelAppearance(float shown)
    {
        var overlay = OpenVR.Overlay;
        foreach (var panel in _panels)
        {
            overlay.SetOverlayAlpha(panel, shown);
            overlay.SetOverlayWidthInMeters(panel, PanelWidthM * (0.94f + 0.06f * shown));
        }
    }

    /// <summary>_panelWorld の所にパネルを置く (開いたとき・動かしたとき)。</summary>
    private void PlacePanel()
    {
        Matrix4x4.Invert(_panelWorld, out _panelInverse);
        var m = ToHmd(_panelWorld);
        foreach (var panel in _panels) // 入れ替えたときにずれないよう、2 枚とも同じ所に置く
            OpenVR.Overlay.SetOverlayTransformAbsolute(panel, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
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

    /// <summary>パネルを動かしている最中 (か、止めた直後) か。この間は描き直さない。</summary>
    private bool MovingPanel => DateTime.UtcNow - _lastPanelMove < MoveSettle;

    private static readonly uint ControllerStateSize = (uint)Marshal.SizeOf<VRControllerState_t>();

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
        // すぐには隠さず、縮みながら消える (AnimatePanelOverlay が最後に隠す)。動きを出さない設定ならすぐ隠す
        StartPanelAppear(closing: true);
        if (!_panelAppear.IsRunning)
            foreach (var panel in _panels) OpenVR.Overlay.HideOverlay(panel);
        _hidePending = false;
        _panelGrabRay = -1;
        _scrollVelocity = 0;
        foreach (var ray in _rays) ray.Pressing = ray.Dragging = false; // 閉じたパネルを、離したときに押さないように
        _panelOpen = false;
        UploadButton(panelOpen: false);
    }

    private VrView View => new(_settings.VrTab, _settings.VrSortKey, _settings.VrGroupView, _filter, _openGroup);

    /// <summary>
    /// 今の見せ方で、並べるタイルを作り直す。
    /// resetScroll なら今着ているアバターが一番上に来るように (タブや並びを変えたとき)、そうでなければ今のスクロール位置のまま。
    /// タブの一覧がまだ手元に無ければ、VRChat へ取りに行き、届いたら作り直す。
    /// </summary>
    private void Reload(bool resetScroll)
    {
        if (_openGroup is not null && !_source.GroupExists(_openGroup)) _openGroup = null;
        var tab = _settings.VrTab;
        _panel.Tab = tab;
        _panel.SortKey = _settings.VrSortKey;
        _panel.SortMenuOpen = false; // 並びを作り直すとき (タブの切り替え・開き直しなど) は閉じる
        _panel.GroupView = _settings.VrGroupView;
        _panel.SetColumns(_settings.VrColumns);
        _panel.OpenGroupName = _openGroup?.Name;

        var items = _source.ItemsOf(tab);
        if (items is null)
        {
            _filter = null;
            _panel.Filter = null;
            _panel.SetChips([]);
            _panel.SetItems([], resetScroll: true);
            LoadTabAsync(tab).Forget();
        }
        else
        {
            var chips = _source.FilterValuesOf(items, tab);
            if (_filter is not null && !chips.Contains(_filter)) _filter = null; // 候補から消えた (タグを外したなど)
            _panel.Filter = _filter;
            _panel.SetChips(chips);
            _panel.SetItems(_source.Arrange(items, View), resetScroll);
        }
        _scrollVelocity = 0;
        _requestedThumbs = (-1, 0); // 並びが入れ替わったので、同じ範囲でも頼み直す
        RequestVisibleThumbnails();
        _panelDirty = true;
    }

    /// <summary>まだ手元に無いタブの一覧を取りに行く。同じタブを重ねて取りには行かない。</summary>
    private async Task LoadTabAsync(string tab)
    {
        if (_loadingTab == tab) return;
        _loadingTab = tab;
        SetStatus("読み込み中…");
        try
        {
            await _source.LoadAsync(tab);
            if (!_connected || !_panelOpen || _settings.VrTab != tab) return; // 待っている間に閉じた / タブを移った
            SetStatus("");
            Reload(resetScroll: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"VR パネル: 一覧 ({tab}) を読み込めませんでした", ex);
            if (_connected && _panelOpen && _settings.VrTab == tab) SetStatus("読み込み失敗: " + FriendlyError.Of(ex), error: true);
        }
        finally { _loadingTab = null; }
    }

    private void SetStatus(string text, bool error = false)
    {
        _panel.Status = text;
        _panel.StatusIsError = error;
        _panelDirty = true;
    }

    /// <summary>
    /// 見えているタイルと、その下の 1 段ぶんのサムネイルを取りに行く (スクロールして出てきたときに空白で待たせないように)。
    /// スクロール中は毎フレーム呼ばれるので、前と同じ範囲なら何もしない。
    /// </summary>
    private void RequestVisibleThumbnails()
    {
        var start = _panel.VisibleStart;
        var count = Math.Min(_panel.VisibleCount + _panel.Columns, _panel.Items.Count - start);
        if ((start, count) == _requestedThumbs) return;
        _requestedThumbs = (start, count);
        for (var i = start; i < start + count; i++) _source.RequestThumbnail(_panel.Items[i]);
    }

    private void Click(VrPanel.Hit hit)
    {
        _panel.NotifyPressed(hit);
        _panelDirty = true;
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
                Reload(resetScroll: true);
                break;
            case VrPanel.HitKind.SortMenu:
                _panel.SortMenuOpen = !_panel.SortMenuOpen;
                break;
            case VrPanel.HitKind.MenuDismiss:
                _panel.SortMenuOpen = false;
                break;
            case VrPanel.HitKind.SortOption when hit.Index >= 0 && hit.Index < VrPanel.SortKinds.Length:
            {
                _panel.SortMenuOpen = false;
                var (kind, _, defaultDesc) = VrPanel.SortKinds[hit.Index];
                // 同じ種類を選び直したときは向きもそのまま。別の種類なら、その種類で自然な向きにする
                if (VrPanel.SortKindOf(_settings.VrSortKey) == kind) break;
                SetSortKey(kind + (defaultDesc ? "_desc" : "_asc"));
                break;
            }
            case VrPanel.HitKind.SortDirection:
            {
                var key = _settings.VrSortKey;
                var kind = VrPanel.SortKindOf(key);
                SetSortKey(kind + (key.EndsWith("_desc", StringComparison.Ordinal) ? "_asc" : "_desc"));
                break;
            }
            case VrPanel.HitKind.ColumnsLess:
            case VrPanel.HitKind.ColumnsMore:
                _settings.VrColumns = Math.Clamp(_settings.VrColumns + (hit.Kind == VrPanel.HitKind.ColumnsLess ? -1 : 1),
                    VrPanel.MinColumns, VrPanel.MaxColumns);
                _source.SaveSettings();
                _panel.SetColumns(_settings.VrColumns);
                RequestVisibleThumbnails();
                break;
            case VrPanel.HitKind.GroupToggle:
                _settings.VrGroupView = !_settings.VrGroupView;
                _openGroup = null;
                _source.SaveSettings();
                Reload(resetScroll: true);
                break;
            case VrPanel.HitKind.Chip:
                _filter = hit.Index <= 0 || hit.Index > _panel.Chips.Count ? null : _panel.Chips[hit.Index - 1];
                Reload(resetScroll: true);
                break;
            case VrPanel.HitKind.ChipPrev:
            case VrPanel.HitKind.ChipNext:
                _panel.ScrollChips(hit.Kind == VrPanel.HitKind.ChipPrev ? -1 : 1);
                break;
            case VrPanel.HitKind.Back:
                var group = _openGroup;
                _openGroup = null;
                Reload(resetScroll: false);
                _panel.ShowItemOf(t => t.Group == group); // 開いていたグループのタイルが見えるところへ
                RequestVisibleThumbnails();
                break;
            case VrPanel.HitKind.Tile when hit.Index >= 0 && hit.Index < _panel.Items.Count:
                var tile = _panel.Items[hit.Index];
                if (tile.Group is { } g)
                {
                    // グループは開いて中身を並べる (着替えるのは中のアバターを選んでから)
                    _openGroup = g;
                    Reload(resetScroll: true);
                }
                else if (_panel.Selected == hit.Index)
                {
                    // 選んでおいたタイルをもう一度押した: チェンジ
                    _panel.Selected = -1;
                    ChangeAsync(tile).Forget();
                }
                else
                {
                    // 1 回目は選ぶだけ (うっかり触れただけで着替えないように。PC のダブルクリックと同じ考え)
                    _panel.Selected = hit.Index;
                    if (!_busy && _loadingTab is null) SetStatus(""); // 下の一言を「もう一度押すとチェンジ」にする
                }
                break;
            default:
                return;
        }
        _panelDirty = true;
    }

    private void SetSortKey(string key)
    {
        _settings.VrSortKey = key;
        _source.SaveSettings();
        Reload(resetScroll: true);
    }

    private async Task ChangeAsync(AvatarItem item)
    {
        if (_busy) return;
        _busy = true;
        SetStatus($"アバターチェンジ中… ({item.Name})");
        bool ok;
        try { ok = await _source.ChangeAsync(item); }
        finally { _busy = false; }
        if (!_connected || !_panelOpen) return;
        SetStatus(ok ? $"アバターチェンジ完了: {item.Name}" : $"アバターチェンジ失敗: {item.Name}", error: !ok);
        // 「今着ている」の印を付け直す (グループのタイルにも)。スクロール位置はそのまま
        if (ok) Reload(resetScroll: false);
        // 着替えても閉じない。見比べながら続けて着替えられるよう、閉じるのは利用者の操作 (閉じるボタン / 手首ボタン / パネルの外を押す) だけ
    }

    /// <summary>
    /// 表示が変わったとき (ホバー・スクロール・サムネイルの到着など) だけ描き直して渡す。毎フレーム呼ばれるので、
    /// 変わっていないかの確認はメモリを確保せずに行い、続けて起きる変化は間隔を空けてまとめる。
    /// </summary>
    private void RefreshPanel()
    {
        if (_hidePending)
        {
            // 入れ替えの途中。古い方を隠し終えるまでは、次の画像を渡さない (渡す先が見えている方になってしまう)
            if (DateTime.UtcNow - _swappedAt < PanelSwapOverlap) return;
            OpenVR.Overlay.HideOverlay(_panels[_front ^ 1]);
            _hidePending = false;
        }
        if (MovingPanel) return; // 止まってからまとめて描き直す
        // 押したとき・ホバーなどの動きは、毎フレーム描き直しても抜けない渡し方 (GPU) のときだけ付ける
        _panel.AnimationsEnabled = _gpu is not null;
        if (_panel.IsAnimating) _panelDirty = true;
        var interval = _panelDirty ? (_gpu is not null ? GpuRenderInterval : RawRenderInterval)
            : ThumbnailsChanged() ? ThumbRenderInterval : (TimeSpan?)null;
        if (interval is null || DateTime.UtcNow - _lastRender < interval) return;
        RenderPanel();
    }

    /// <summary>描いたあとに、見えているタイルのサムネイルが届いた (差し替わった) か。</summary>
    private bool ThumbnailsChanged()
    {
        for (var slot = 0; slot < _renderedThumbs.Length; slot++)
        {
            var now = slot < _panel.VisibleCount ? _panel.VisibleItem(slot).Thumbnail : null;
            if (!ReferenceEquals(now, _renderedThumbs[slot])) return true;
        }
        return false;
    }

    private void RenderPanel()
    {
        // 前に渡したものと同じ画像なら渡し直さない (差し替えるたびに一瞬表示が抜けることがあるので、要らない差し替えはしない)
        var rgba = _panel.RenderRgba();
        if (CopyIfChanged(rgba, _uploadedPanel, _uploadedPanelValid))
        {
            // GPU で渡せるなら、見せている方にそのまま渡す (SteamVR は新しい画像の用意ができるまで前の画像を出し続けるので抜けない)
            if (_gpu is not null)
            {
                if (_gpu.Upload(_panels[_front], rgba, VrPanel.Width, VrPanel.Height))
                {
                    FinishRender();
                    return;
                }
                // 渡せなかった (GPU の不調など)。以後はこの接続の間、生データ + 2 枚の入れ替えで渡す
                _gpu.Dispose();
                _gpu = null;
            }
            // 見えていない方に渡してから入れ替える (見えている方を生データで差し替えると、途中で一瞬抜けてちらつく)
            var back = _front ^ 1;
            Upload(_panels[back], rgba, VrPanel.Width, VrPanel.Height, () => _panel.Render());
            _uploadedPanelValid = true;
            if (_panelOpen)
            {
                // 新しい方を手前に重ねて出す。古い方は少し待ってから隠す (RefreshPanel)。その間も必ずどちらかが映っている
                var overlay = OpenVR.Overlay;
                overlay.SetOverlaySortOrder(_panels[back], 1);
                overlay.SetOverlaySortOrder(_panels[_front], 0);
                overlay.ShowOverlay(_panels[back]);
                _hidePending = true;
                _swappedAt = DateTime.UtcNow;
            }
            _front = back; // 閉じているときは入れ替えるだけ (開くときに _front を出す)
        }
        FinishRender();
    }

    /// <summary>描いたときの状態を覚えておく (次に描き直しが要るかの判断に使う)。</summary>
    private void FinishRender()
    {
        for (var slot = 0; slot < _renderedThumbs.Length; slot++)
            _renderedThumbs[slot] = slot < _panel.VisibleCount ? _panel.VisibleItem(slot).Thumbnail : null;
        _panelDirty = false;
        _lastRender = DateTime.UtcNow;
    }

    /// <summary>
    /// next が last と違えば (または last がまだ無ければ) last に写して true。同じなら何もせず false。
    /// 2MB の比較は 0.1ms ほどで、SteamVR への差し替えよりずっと安い。
    /// </summary>
    internal static bool CopyIfChanged(byte[] next, byte[] last, bool lastValid)
    {
        if (lastValid && next.AsSpan().SequenceEqual(last)) return false;
        next.AsSpan().CopyTo(last);
        return true;
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
    internal void ReloadForTest() => Reload(resetScroll: true);
    internal void SetOpenForTest(bool open) { _panelOpen = open; _connected = open; }
    /// <summary>検証用: パネル 1 回ぶんの描き直し (SteamVR に渡す直前まで) を行い、渡すバイト数を返す。</summary>
    internal int RenderPanelBytesForTest() => _panel.RenderRgba().Length;
    internal static Matrix4x4 ButtonOffsetForTest(Settings s) => new VrOverlay(s, null!).ButtonOffset();
    internal static HmdMatrix34_t ToHmdForTest(Matrix4x4 m) => ToHmd(m);
    internal static Matrix4x4 ToNumericsForTest(HmdMatrix34_t m) => ToNumerics(m);
    internal const float PanelWidthForTest = PanelWidthM;
#endif
}
