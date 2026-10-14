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
/// </summary>
internal sealed partial class VrOverlay
{
    private const string CursorKey = "vrcavatarchanger.cursor";
    private const float MaxRayM = 3f;              // これより遠くは指しても当たらない
    private const float PanelCursorWidthM = 0.016f;
    private const float ButtonCursorWidthM = 0.007f;
    private const float RayButtonSlack = 1.6f;     // ボタンは小さいので、見た目より少し広く当たりを取る
    private const float StickPageThreshold = 0.7f; // パネルを指してスティックをこれ以上倒すと、ページをめくる

    private enum RayTarget { None, Button, Panel }

    private ulong _cursor;
    private bool _cursorShown;
    private float _cursorWidth;
    private uint _pointer = OpenVR.k_unTrackedDeviceIndexInvalid; // 今指している手 (両手で指したときは先に指した方)
    private RayTarget _rayTarget;
    private readonly Dictionary<uint, bool> _triggerWasDown = [];
    private readonly Dictionary<uint, bool> _stickWasTilted = [];
    private readonly Dictionary<uint, Matrix4x4?> _tipOffset = []; // 機器ごとの、原点から「指す先」(tip) への位置と向き

    // UpdateButton が毎フレーム書く、手首ボタンの今の姿勢と見えているか
    private Matrix4x4 _buttonWorld;
    private bool _buttonVisible;

    /// <summary>反対の手のレイが手首ボタンに当たっているか (つかむ判定に使う)。</summary>
    private bool RayOnButtonBy(uint device) => _rayTarget == RayTarget.Button && _pointer == device;

    private void CreateCursor()
    {
        var overlay = OpenVR.Overlay;
        Check(overlay.CreateOverlay(CursorKey, "AvatarChanger カーソル", ref _cursor), "カーソルの作成");
        overlay.SetOverlaySortOrder(_cursor, 100); // パネルやボタンより手前に
        Upload(_cursor, VrPanel.CursorRgba(), VrPanel.CursorSize, VrPanel.CursorSize, VrPanel.RenderCursor);
    }

    private void ResetRay()
    {
        _cursor = 0;
        _cursorShown = false;
        _cursorWidth = 0;
        _pointer = OpenVR.k_unTrackedDeviceIndexInvalid;
        _rayTarget = RayTarget.None;
        _triggerWasDown.Clear();
        _stickWasTilted.Clear();
        _tipOffset.Clear();
        _buttonVisible = false;
    }

    /// <summary>
    /// 両手のレイを手首ボタンとパネルに当てて、当たった所にカーソルを出す。
    /// トリガーを引いた瞬間に押し、パネルを指したままスティックを上下に倒すとページをめくる。
    /// </summary>
    private void UpdateRay()
    {
        var buttonHand = RightHanded ? _rightHand : _leftHand;
        var target = RayTarget.None;
        var device = OpenVR.k_unTrackedDeviceIndexInvalid;
        var at = default(System.Windows.Point);
        var point = Vector3.Zero;
        var best = float.MaxValue;
        foreach (var role in Hands)
        {
            var d = HandDevice(role);
            if (!IsValid(d)) continue;
            var (origin, direction) = RayOf(d);
            if (_panelOpen && RayHit(_panelInverse, PanelWidthM, VrPanel.Width, VrPanel.Height, origin, direction, 1, out var pAt, out var pDist))
                Consider(d, RayTarget.Panel, pAt, origin + direction * pDist, pDist);
            // ボタンは付けている手では指せない (自分の手の甲)。見えていないときも当てない
            if (_buttonVisible && d != buttonHand && Matrix4x4.Invert(_buttonWorld, out var buttonInverse)
                && RayHit(buttonInverse, ButtonWidthM, VrPanel.ButtonSize, VrPanel.ButtonSize, origin, direction, RayButtonSlack, out var bAt, out var bDist))
                Consider(d, RayTarget.Button, bAt, origin + direction * bDist, bDist);
        }

        void Consider(uint d, RayTarget t, System.Windows.Point hitAt, Vector3 hitPoint, float distance)
        {
            // 両手で指しているときは、先に指していた手を優先する (カーソルが手の間で跳ねないように)
            var score = d == _pointer ? distance - 10 : distance;
            if (score >= best) return;
            best = score;
            target = t;
            device = d;
            at = hitAt;
            point = hitPoint;
        }

        _pointer = device;
        _rayTarget = target;
        UpdatePanelHoverByRay(target == RayTarget.Panel ? _panel.HitTest(at.X, at.Y) : default);
        ShowCursor(target, point);

        // トリガー: 指している手が引いた瞬間に押す (指していない手の分も、引いたままの状態を覚えておく)
        foreach (var role in Hands)
        {
            var d = HandDevice(role);
            if (!IsValid(d)) continue;
            var state = new VRControllerState_t();
            if (!OpenVR.System.GetControllerState(d, ref state, ControllerStateSize)) continue;
            var down = (state.ulButtonPressed & (1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger)) != 0;
            var pressed = down && !_triggerWasDown.GetValueOrDefault(d);
            _triggerWasDown[d] = down;

            var stickY = StickY(d, state);
            var tilted = MathF.Abs(stickY) >= StickPageThreshold;
            var flicked = tilted && !_stickWasTilted.GetValueOrDefault(d);
            _stickWasTilted[d] = tilted;

            if (d != device) continue;
            if (pressed && target == RayTarget.Button)
            {
                Log.Info("VR: 手首ボタンが押されました");
                Press(TogglePanel);
            }
            else if (pressed && target == RayTarget.Panel)
            {
                var hit = _panel.HitTest(at.X, at.Y);
                Press(() => Click(hit));
            }
            // グリップを握っているときのスティックはパネルの距離 (MovePanelByStick) なので、めくらない
            else if (flicked && target == RayTarget.Panel && (state.ulButtonPressed & (1UL << (int)EVRButtonId.k_EButton_Grip)) == 0)
                Click(new VrPanel.Hit(stickY > 0 ? VrPanel.HitKind.Prev : VrPanel.HitKind.Next)); // 上に倒すと前のページ
        }
    }

    /// <summary>レイの当たっている所をパネルのホバーにする。パネルを動かしている間は変えない (ちらつくため)。</summary>
    private void UpdatePanelHoverByRay(VrPanel.Hit hover)
    {
        if (!_panelOpen || MovingPanel || hover == _panel.Hover) return;
        _panel.Hover = hover;
        _panelDirty = true;
    }

    private void ShowCursor(RayTarget target, Vector3 point)
    {
        var overlay = OpenVR.Overlay;
        if (target == RayTarget.None)
        {
            if (_cursorShown) { overlay.HideOverlay(_cursor); _cursorShown = false; }
            return;
        }
        var surface = target == RayTarget.Panel ? _panelWorld : _buttonWorld;
        var width = target == RayTarget.Panel ? PanelCursorWidthM : ButtonCursorWidthM;
        if (width != _cursorWidth) { overlay.SetOverlayWidthInMeters(_cursor, width); _cursorWidth = width; }
        // 面と同じ向きで、面から少しだけ浮かせて置く (面に埋もれないように)
        var pose = surface;
        pose.Translation = point + Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, surface)) * 0.003f;
        var m = ToHmd(pose);
        overlay.SetOverlayTransformAbsolute(_cursor, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
        if (!_cursorShown) { overlay.ShowOverlay(_cursor); _cursorShown = true; }
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
