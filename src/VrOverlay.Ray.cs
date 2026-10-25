using System.Numerics;
using System.Text;
using Valve.VR;

namespace VRCAvatarChanger;

/// <summary>
/// 指して操作する (レイ)。OVR Toolkit と同じく、SteamVR のレーザー (マウス入力) は使わず、
/// コントローラーの向きから自分で当たりを計算し、当たった所にカーソルを出して、トリガーで押す。
/// SteamVR のレーザーを使うと、パネルやボタンを指した瞬間に SteamVR がコントローラーの入力を奪い、
/// VRChat に届かなくなる (プッシュトゥトークが切れる)。自前で計算すれば入力は奪わない
/// (ボタンの状態は読むだけなので、トリガーは VRChat にも届く。OVR Toolkit と同じ)。
///
/// レイは左右の手それぞれに持ち、どちらの手でも指して押せる (カーソルも手ごとに出す)。
/// パネルを指してグリップを握るとつかめ、手について動く (面はいつも顔の方を向く)。
/// </summary>
internal sealed partial class VrOverlay
{
    private const string CursorKey = "vrcavatarchanger.cursor";
    private const float MaxRayM = 3f;              // これより遠くは指しても当たらない
    private const float PanelCursorWidthM = 0.016f;
    private const float ButtonCursorWidthM = 0.007f;
    private const float RayButtonSlack = 1.6f;     // ボタンは小さいので、見た目より少し広く当たりを取る
    private const float StickScrollSpeed = 1400f;  // パネルを指してスティックをいっぱいに倒したときのスクロールの速さ (px/秒)
    private const double DragThreshold = 14;       // トリガーを引いたままこれ以上 (px) 動かしたら、押すのではなくドラッグでスクロール
    private const double MaxFlingSpeed = 4000;     // 離したときに流れる速さの上限 (px/秒)
    private const ushort PressPulseMicroSec = 2500;  // 押したときの振動の長さ (SteamVR の 1 回の上限は 3999µs)
    private const float MinGrabReachM = 0.1f;      // つかんだパネルは、手からこの距離より近くには寄せない

    private enum RayTarget { None, Button, Panel }

    /// <summary>片手ぶんのレイの状態 (左右で 1 つずつ。Hands と同じ並び)。</summary>
    private sealed class HandRay
    {
        public uint Device = OpenVR.k_unTrackedDeviceIndexInvalid;
        public RayTarget Target;
        public System.Windows.Point At;   // パネルの画像の座標 (Target が Panel のとき)
        public Vector3 Point;             // 当たった所 (ワールド)
        public ulong Cursor;
        public bool CursorShown;
        public float CursorWidth;
        public bool TriggerWasDown;
        public bool GripWasDown;
        // タイルの窓でトリガーを引いた: 動かさずに離せば押す、動かせばドラッグでスクロール
        public bool Pressing;
        public bool Dragging;
        public VrPanel.Hit PressHit;
        public double PressY;          // 引いたときの画像の Y と、そのときのスクロール位置
        public double PressScroll;
        public double LastY;
        public double Velocity;        // ドラッグの速さ (スクロール位置の px/秒)。離したときに流す
    }

    private readonly HandRay[] _rays = [new(), new()];
    private int _activeRay = -1;      // パネルのホバーを出す手 (最後に指し始めた / 押した方)
    private readonly Dictionary<uint, Matrix4x4?> _tipOffset = []; // 機器ごとの、原点から「指す先」(tip) への位置と向き

    // つかんだパネル
    private int _panelGrabRay = -1;   // つかんでいる手 (_rays の番号、-1 = つかんでいない)
    private Vector3 _panelGrabOffset; // つかんだ手から見たパネルの中心

    // UpdateButton が毎フレーム書く、手首ボタンの今の姿勢と見えているか
    private Matrix4x4 _buttonWorld;
    private bool _buttonVisible;

    /// <summary>その手のレイが手首ボタンに当たっているか (つかむ判定に使う)。</summary>
    private bool RayOnButtonBy(uint device) => _rays.Any(r => r.Device == device && r.Target == RayTarget.Button);

    private bool GrabbingPanel => _panelGrabRay >= 0;

    private void CreateCursor()
    {
        var overlay = OpenVR.Overlay;
        for (var i = 0; i < _rays.Length; i++)
        {
            Check(overlay.CreateOverlay($"{CursorKey}.{i}", "AvatarChanger カーソル", ref _rays[i].Cursor), "カーソルの作成");
            overlay.SetOverlaySortOrder(_rays[i].Cursor, 100); // パネルやボタンより手前に
            Upload(_rays[i].Cursor, VrPanel.CursorRgba(), VrPanel.CursorSize, VrPanel.CursorSize, VrPanel.RenderCursor);
        }
    }

    private void DestroyCursors()
    {
        foreach (var ray in _rays)
            if (ray.Cursor != 0) OpenVR.Overlay.DestroyOverlay(ray.Cursor);
    }

    private void ResetRay()
    {
        for (var i = 0; i < _rays.Length; i++) _rays[i] = new HandRay();
        _activeRay = -1;
        _panelGrabRay = -1;
        _tipOffset.Clear();
        _buttonVisible = false;
    }

    /// <summary>
    /// 左右それぞれのレイを手首ボタンとパネルに当てて、当たった所にカーソルを出す。どちらの手でも:
    /// ・トリガーで押す (タイルの窓では、動かさずに離したら押す / 引いたまま上下に動かすとドラッグでスクロール)
    /// ・パネルを指したままスティックを上下に倒すとスクロール
    /// ・パネルを指してグリップを握るとつかむ
    /// ・パネルを開いている間に、パネルの外 (何も指していない所) でトリガーを引くと閉じる
    /// </summary>
    private void UpdateRay(float dt)
    {
        var buttonHand = RightHanded ? _rightHand : _leftHand;
        for (var i = 0; i < _rays.Length; i++)
        {
            var ray = _rays[i];
            var d = HandDevice(Hands[i]);
            ray.Device = d;
            var wasOnPanel = ray.Target == RayTarget.Panel;
            ray.Target = RayTarget.None;
            if (IsValid(d))
            {
                var (origin, direction) = RayOf(d);
                var best = float.MaxValue;
                if (_panelOpen && RayHit(_panelInverse, PanelWidthM, VrPanel.Width, VrPanel.Height, origin, direction, 1, out var pAt, out var pDist))
                {
                    best = pDist;
                    ray.Target = RayTarget.Panel;
                    ray.At = pAt;
                    ray.Point = origin + direction * pDist;
                }
                // ボタンは付けている手では指せない (自分の手の甲)。見えていないときも当てない
                if (_buttonVisible && d != buttonHand && Matrix4x4.Invert(_buttonWorld, out var buttonInverse)
                    && RayHit(buttonInverse, ButtonWidthM, VrPanel.ButtonSize, VrPanel.ButtonSize, origin, direction, RayButtonSlack, out var bAt, out var bDist)
                    && bDist < best)
                {
                    ray.Target = RayTarget.Button;
                    ray.At = bAt;
                    ray.Point = origin + direction * bDist;
                }
            }
            // パネルのホバーは 1 つだけなので、新しくパネルを指し始めた手に移す (指している手が 1 本なら、その手)
            if (ray.Target == RayTarget.Panel && (!wasOnPanel || _activeRay < 0 || _rays[_activeRay].Target != RayTarget.Panel))
                _activeRay = i;
            ShowCursor(ray);
        }
        if (_activeRay >= 0 && _rays[_activeRay].Target != RayTarget.Panel)
            _activeRay = Array.FindIndex(_rays, r => r.Target == RayTarget.Panel);
        UpdatePanelHoverByRay(_activeRay >= 0 ? _panel.HitTest(_rays[_activeRay].At.X, _rays[_activeRay].At.Y) : default);

        for (var i = 0; i < _rays.Length; i++)
        {
            var ray = _rays[i];
            var d = ray.Device;
            if (!IsValid(d)) { ray.TriggerWasDown = ray.GripWasDown = ray.Pressing = ray.Dragging = false; continue; }
            var state = new VRControllerState_t();
            if (!OpenVR.System.GetControllerState(d, ref state, ControllerStateSize)) continue;
            var trigger = (state.ulButtonPressed & (1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger)) != 0;
            var grip = (state.ulButtonPressed & (1UL << (int)EVRButtonId.k_EButton_Grip)) != 0;
            var pressed = trigger && !ray.TriggerWasDown;
            var gripped = grip && !ray.GripWasDown;
            ray.TriggerWasDown = trigger;
            ray.GripWasDown = grip;

            if (ray.Pressing)
            {
                // タイルの窓で引いたトリガー: 引いている間は動きを見て、離したときに押すかスクロールを流すか決める
                if (trigger) { TrackDrag(ray, dt); continue; }
                ray.Pressing = false;
                if (ray.Dragging)
                {
                    ray.Dragging = false;
                    _scrollVelocity = Math.Clamp(ray.Velocity, -MaxFlingSpeed, MaxFlingSpeed);
                }
                else if (ray.PressHit != default)
                {
                    Buzz(d);
                    var hit = ray.PressHit;
                    Click(hit); // パネルの中は待ち時間をかけない (すばやい 2 回押しでチェンジできるように。押した瞬間を 1 回だけ見ているので二重には反応しない)
                }
                continue;
            }

            if (i == _panelGrabRay)
            {
                // つかんでいる手: 握っている間は手についてくる。スティックの前後で遠ざける / 近づける
                if (grip) continue;
                _panelGrabRay = -1;
                _panelDistance = Math.Clamp(Vector3.Distance(_panelWorld.Translation, HeadPosition()), MinPanelDistanceM, MaxPanelDistanceM);
                continue;
            }
            if (pressed && ray.Target == RayTarget.Button)
            {
                Log.Info("VR: 手首ボタンが押されました");
                Buzz(d);
                Press(TogglePanel);
            }
            else if (pressed && ray.Target == RayTarget.Panel)
            {
                _activeRay = i;
                var hit = _panel.HitTest(ray.At.X, ray.At.Y);
                if (VrPanel.InGrid(ray.At.X, ray.At.Y) && !_panel.SortMenuOpen) // リストを開いている間は、その上を押すだけ
                {
                    // タイルの窓: まだ押さない (離すまでに動かしたらドラッグ)。流れているスクロールは止める
                    ray.Pressing = true;
                    ray.Dragging = false;
                    ray.PressHit = hit;
                    ray.PressY = ray.LastY = ray.At.Y;
                    ray.PressScroll = _panel.ScrollY;
                    ray.Velocity = 0;
                    _scrollVelocity = 0;
                }
                else
                {
                    if (hit != default) Buzz(d); // 何も無い所を押したときは震わせない
                    Click(hit); // パネルの中は待ち時間をかけない (すばやい 2 回押しでチェンジできるように。押した瞬間を 1 回だけ見ているので二重には反応しない)
                }
            }
            else if (pressed && ray.Target == RayTarget.None && _panelOpen && !GrabbingPanel)
            {
                // パネルの外を押した: 閉じる (つかんで動かしている間は閉じない)
                Press(ClosePanel);
            }
            else if (gripped && ray.Target == RayTarget.Panel && !GrabbingPanel)
            {
                // パネルを指してグリップを握った: つかむ
                _panelGrabRay = i;
                _panelGrabOffset = Vector3.Transform(_panelWorld.Translation, Inverse(ToNumerics(_poses[d].mDeviceToAbsoluteTracking)));
                Buzz(d);
            }
            // グリップを握っているとき (VRChat で物をつかんでいるときなど) のスティックではスクロールしない
            else if (ray.Target == RayTarget.Panel && !grip && !_panel.SortMenuOpen)
            {
                var tilt = StickY(d, state);
                if (MathF.Abs(tilt) >= StickDeadZone)
                {
                    // 上に倒すと上へ (前のタイルへ)。遊びの端から 0 で立ち上がるようにする
                    var amount = (MathF.Abs(tilt) - StickDeadZone) / (1 - StickDeadZone) * MathF.Sign(tilt);
                    _scrollVelocity = 0;
                    if (_panel.ScrollBy(-amount * StickScrollSpeed * dt)) OnScrolled();
                }
            }
        }
    }

    /// <summary>トリガーを引いたまま動かしているレイ: 一定以上動いたらドラッグにして、指している所に並びをついてこさせる。</summary>
    private void TrackDrag(HandRay ray, float dt)
    {
        if (ray.Target != RayTarget.Panel) return; // パネルの外に出ている間は止めておく (戻ってきたら続ける)
        var y = ray.At.Y;
        if (!ray.Dragging && Math.Abs(y - ray.PressY) > DragThreshold)
        {
            ray.Dragging = true;
            // 押そうとしていたタイルを選ばないよう、ここからは押す扱いにしない
            ray.PressHit = default;
        }
        if (ray.Dragging)
        {
            if (dt > 0) ray.Velocity = ray.Velocity * 0.5 + -(y - ray.LastY) / dt * 0.5; // 離した瞬間の揺れで流れ方が暴れないよう、ならす
            if (_panel.SetScroll(ray.PressScroll - (y - ray.PressY))) OnScrolled();
        }
        ray.LastY = y;
    }

    /// <summary>ドラッグを離したあとの、流れるスクロール (だんだん止まる)。</summary>
    private void ApplyScrollInertia(float dt)
    {
        if (_scrollVelocity == 0) return;
        if (Math.Abs(_scrollVelocity) < 30 || !_panel.ScrollBy(_scrollVelocity * dt))
        {
            _scrollVelocity = 0; // 遅くなった / 端に着いた
            return;
        }
        _scrollVelocity *= Math.Exp(-ScrollFriction * dt);
        OnScrolled();
    }

    /// <summary>スクロールしたあと: 描き直しを頼み、新しく見えたタイルのサムネイルを取りに行く。</summary>
    private void OnScrolled()
    {
        _panelDirty = true;
        RequestVisibleThumbnails();
    }

    /// <summary>ドラッグしているか、離したあとに流れているか (この間はホバーを出さない)。</summary>
    private bool Scrolling => _scrollVelocity != 0 || _rays.Any(r => r.Dragging);

    /// <summary>
    /// つかんでいるパネルを手について動かす。面はいつも顔の方へ向ける (手首をひねっても傾かない)。
    /// つかんだままスティックを前後に倒すと、手から遠ざける / 近づける (前に倒すと遠く)。
    /// </summary>
    private void MoveGrabbedPanel(float dt)
    {
        if (!GrabbingPanel) return;
        var d = _rays[_panelGrabRay].Device;
        if (!IsValid(d)) { _panelGrabRay = -1; return; } // 見失った手では動かさない (その場に残す)
        var hand = ToNumerics(_poses[d].mDeviceToAbsoluteTracking);
        var state = new VRControllerState_t();
        if (OpenVR.System.GetControllerState(d, ref state, ControllerStateSize))
        {
            var reach = _panelGrabOffset.Length();
            var next = NextGrabReach(reach, StickY(d, state), dt);
            if (next != reach && reach > 1e-4f) _panelGrabOffset *= next / reach;
        }
        _panelWorld = FacingPose(Vector3.Transform(_panelGrabOffset, hand), HeadPosition());
        PlacePanel();
        _lastPanelMove = DateTime.UtcNow;
        if (_panel.Hover != default) { _panel.Hover = default; _panelDirty = true; }
    }

    /// <summary>つかんだパネルの手からの距離を、スティックの傾きで変える (遊び・上限はパネルの距離と同じ考え)。</summary>
    internal static float NextGrabReach(float reach, float tilt, float dt)
    {
        if (MathF.Abs(tilt) < StickDeadZone) return reach;
        var amount = (MathF.Abs(tilt) - StickDeadZone) / (1 - StickDeadZone) * MathF.Sign(tilt);
        return Math.Clamp(reach + amount * PanelMoveSpeed * dt, MinGrabReachM, MaxRayM - 0.2f);
    }

    /// <summary>position に置き、表 (+Z) を head へ向けた姿勢 (画像の上は空の側)。</summary>
    internal static Matrix4x4 FacingPose(Vector3 position, Vector3 head)
    {
        var away = position - head; // 面の裏 (-Z) の向き
        if (away.LengthSquared() < 1e-6f) away = -Vector3.UnitZ;
        away = Vector3.Normalize(away);
        // 真上・真下を向けると上下が決まらないので、そのときは手前を上とみなす
        var up = MathF.Abs(Vector3.Dot(away, Vector3.UnitY)) > 0.98f ? Vector3.Normalize(new Vector3(away.X, 0, away.Z) + Vector3.UnitZ * 1e-3f) : Vector3.UnitY;
        return Matrix4x4.CreateWorld(position, away, up);
    }

    private Vector3 HeadPosition() => ToNumerics(_poses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking).Translation;

    /// <summary>押したことが手に伝わるよう、コントローラーを短く震わせる (OVR Toolkit と同じ)。</summary>
    private static void Buzz(uint device)
    {
        try { OpenVR.System.TriggerHapticPulse(device, 0, PressPulseMicroSec); }
        catch (Exception ex) { Log.Debug("VR: コントローラーを振動させられませんでした", ex); }
    }

    /// <summary>レイの当たっている所をパネルのホバーにする。パネルを動かしている間は変えない (ちらつくため)。</summary>
    private void UpdatePanelHoverByRay(VrPanel.Hit hover)
    {
        if (Scrolling) hover = default; // 並びが動いている間は、レイの下のタイルが次々に変わってうるさいので出さない
        if (!_panelOpen || MovingPanel || hover == _panel.Hover) return;
        _panel.Hover = hover;
        _panelDirty = true;
    }

    private void ShowCursor(HandRay ray)
    {
        var overlay = OpenVR.Overlay;
        if (ray.Target == RayTarget.None)
        {
            if (ray.CursorShown) { overlay.HideOverlay(ray.Cursor); ray.CursorShown = false; }
            return;
        }
        var surface = ray.Target == RayTarget.Panel ? _panelWorld : _buttonWorld;
        var width = ray.Target == RayTarget.Panel ? PanelCursorWidthM : ButtonCursorWidthM;
        if (width != ray.CursorWidth) { overlay.SetOverlayWidthInMeters(ray.Cursor, width); ray.CursorWidth = width; }
        // 面と同じ向きで、面から少しだけ浮かせて置く (面に埋もれないように)
        var pose = surface;
        pose.Translation = ray.Point + Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, surface)) * 0.003f;
        var m = ToHmd(pose);
        overlay.SetOverlayTransformAbsolute(ray.Cursor, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
        if (!ray.CursorShown) { overlay.ShowOverlay(ray.Cursor); ray.CursorShown = true; }
    }

    /// <summary>コントローラーのレイ (始点と向き、ワールド座標)。SteamVR のレーザーと同じ「指す先 (tip)」から出す。</summary>
    private (Vector3 Origin, Vector3 Direction) RayOf(uint device)
    {
        if (!_tipOffset.TryGetValue(device, out var tip))
        {
            tip = TipOffset(device);
            _tipOffset[device] = tip;
        }
        var pose = (tip ?? Matrix4x4.Identity) * ToNumerics(_poses[device].mDeviceToAbsoluteTracking);
        return (pose.Translation, Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, pose)));
    }

    /// <summary>
    /// コントローラーの原点から「tip」(SteamVR のレーザーが出る所。-Z が指す向き) への位置と向き。
    /// 原点の向きのままだと、Quest のコントローラーなどでは指す向きが大きくずれるため、コントローラーの模型の情報から取る。
    /// 取れなければ null (原点からまっすぐ -Z)。
    /// </summary>
    private static Matrix4x4? TipOffset(uint device)
    {
        try
        {
            var err = ETrackedPropertyError.TrackedProp_Success;
            var name = new StringBuilder(256);
            OpenVR.System.GetStringTrackedDeviceProperty(device, ETrackedDeviceProperty.Prop_RenderModelName_String, name, (uint)name.Capacity, ref err);
            if (err != ETrackedPropertyError.TrackedProp_Success || name.Length == 0) return null;
            var controller = new VRControllerState_t();
            var mode = new RenderModel_ControllerMode_State_t();
            var component = new RenderModel_ComponentState_t();
            if (!OpenVR.RenderModels.GetComponentState(name.ToString(), "tip", ref controller, ref mode, ref component)) return null;
            var tip = ToNumerics(component.mTrackingToComponentLocal);
            Log.Debug($"VR: レイの出る位置 ({name}) ({tip.Translation.X:F3}, {tip.Translation.Y:F3}, {tip.Translation.Z:F3})");
            return tip;
        }
        catch (Exception ex)
        {
            Log.Debug("VR: コントローラーの指す向きを取れませんでした (原点の向きで代用します)", ex);
            return null;
        }
    }

    /// <summary>
    /// レイが面の表に当たるか。当たれば、その場所を画像の座標 (px、左上が原点) と、始点からの距離 (m) で返す。
    /// slack は当たりを見た目より広く取る倍率 (1 = 見た目どおり)。広げた分は面の端に寄せる。
    /// </summary>
    /// <param name="surfaceInverse">面の姿勢の逆行列 (ワールド → 面の座標。中心が原点、+Z が表)</param>
    internal static bool RayHit(Matrix4x4 surfaceInverse, float widthM, int texWidth, int texHeight, Vector3 origin, Vector3 direction,
        float slack, out System.Windows.Point at, out float distance)
    {
        at = default;
        distance = 0;
        var o = Vector3.Transform(origin, surfaceInverse);
        var d = Vector3.TransformNormal(direction, surfaceInverse);
        if (o.Z <= 0 || d.Z >= -1e-4f) return false; // 裏側から / 面と平行・遠ざかる向き
        var t = -o.Z / d.Z;
        if (t > MaxRayM) return false;
        var p = o + d * t;
        var heightM = widthM * texHeight / texWidth;
        if (MathF.Abs(p.X) > widthM / 2 * slack || MathF.Abs(p.Y) > heightM / 2 * slack) return false;
        var x = Math.Clamp(p.X, -widthM / 2, widthM / 2);
        var y = Math.Clamp(p.Y, -heightM / 2, heightM / 2);
        at = new System.Windows.Point((x / widthM + 0.5) * texWidth, (0.5 - y / heightM) * texHeight);
        distance = t; // 面の座標は回転と移動だけなので、t はワールドでの距離 (direction が単位ベクトルのとき)
        return true;
    }
}
