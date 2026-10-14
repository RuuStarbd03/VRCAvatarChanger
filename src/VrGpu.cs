using System.Runtime.InteropServices;
using Valve.VR;

namespace VRCAvatarChanger;

/// <summary>
/// VR パネルの画像を GPU (Direct3D 11) のテクスチャ経由で SteamVR に渡す。
/// SetOverlayRaw (生データ) は差し替えの途中で一瞬抜けることがあり、大きな画像を何度も渡すのにも向かない。
/// テクスチャで渡せば、SteamVR は新しい画像の用意ができるまで前の画像を出し続けるので、
/// スクロールのように毎フレーム描き直しても抜けない (OVR Toolkit なども同じ渡し方)。
///
/// D3D11 は COM の関数表 (vtable) を直接呼ぶ。使うのは デバイスの作成・テクスチャの作成・中身の更新・解放 だけ。
/// 作れなかったとき (GPU の不調など) は null を返し、呼び出し側は生データでの渡し方に戻す。
/// SteamVR と同じく UI スレッドからだけ使う。
/// </summary>
internal sealed unsafe class VrGpu : IDisposable
{
    private const int DxgiFormatR8G8B8A8Unorm = 28; // VrPanel が作る並び (RGBA) のまま渡す
    private const uint BindShaderResource = 0x8;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint SdkVersion = 7;

    // vtable の番号 (d3d11.h / dxgi.h の宣言順)
    private const int VtRelease = 2;
    private const int VtDeviceCreateTexture2D = 5;
    private const int VtContextUpdateSubresource = 48;
    private const int VtContextFlush = 111;
    private const int VtFactoryEnumAdapters = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount,
        uint sdkVersion, out nint device, out int featureLevel, out nint immediateContext);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private nint _device;
    private nint _context;
    private readonly Dictionary<ulong, (nint Texture, int Width, int Height)> _textures = [];

    private VrGpu(nint device, nint context)
    {
        _device = device;
        _context = context;
    }

    /// <summary>
    /// SteamVR が画面を出している GPU (adapterIndex、分からなければ -1 で既定の GPU) にデバイスを作る。
    /// HMD と違う GPU で作ったテクスチャは SteamVR が受け取れないことがある (GPU が 2 つあるノート PC など)。
    /// </summary>
    public static VrGpu? Create(int adapterIndex)
    {
        nint adapter = 0;
        try
        {
            if (adapterIndex >= 0 && CreateDXGIFactory1(IidDxgiFactory1, out var factory) >= 0)
            {
                var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtable(factory, VtFactoryEnumAdapters);
                nint found;
                if (enumAdapters(factory, (uint)adapterIndex, &found) >= 0) adapter = found;
                Release(factory);
            }
            // adapter を指定するときは driverType = UNKNOWN (0)、指定しないときは HARDWARE (1)
            var hr = D3D11CreateDevice(adapter, adapter != 0 ? 0 : 1, 0, CreateDeviceBgraSupport, 0, 0, SdkVersion,
                out var device, out _, out var context);
            if (hr < 0)
            {
                Log.Warn($"VR: GPU (Direct3D 11) を使えませんでした (0x{hr:X8})。パネルは生データで渡します");
                return null;
            }
            return new VrGpu(device, context);
        }
        catch (Exception ex)
        {
            Log.Warn("VR: GPU (Direct3D 11) を使えませんでした。パネルは生データで渡します", ex);
            return null;
        }
        finally
        {
            if (adapter != 0) Release(adapter);
        }
    }

    /// <summary>
    /// rgba (RGBA、上の行から) をそのオーバーレイ用のテクスチャに書き、SteamVR に渡す。
    /// テクスチャはオーバーレイごとに 1 枚作って使い回す。渡せなければ false (呼び出し側が生データで渡し直す)。
    /// </summary>
    public bool Upload(ulong overlay, byte[] rgba, int width, int height)
    {
        if (_device == 0) return false;
        if (!_textures.TryGetValue(overlay, out var entry) || entry.Width != width || entry.Height != height)
        {
            if (entry.Texture != 0) Release(entry.Texture);
            var desc = new Texture2DDesc
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = DxgiFormatR8G8B8A8Unorm, SampleCount = 1, Usage = 0 /* DEFAULT */, BindFlags = BindShaderResource,
            };
            var create = (delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Vtable(_device, VtDeviceCreateTexture2D);
            nint texture;
            var hr = create(_device, &desc, 0, &texture);
            if (hr < 0)
            {
                _textures.Remove(overlay);
                Log.Warn($"VR: テクスチャを作れませんでした (0x{hr:X8})");
                return false;
            }
            entry = (texture, width, height);
            _textures[overlay] = entry;
        }

        var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Vtable(_context, VtContextUpdateSubresource);
        fixed (byte* p = rgba) update(_context, entry.Texture, 0, 0, p, (uint)(width * 4), 0);
        ((delegate* unmanaged[Stdcall]<nint, void>)Vtable(_context, VtContextFlush))(_context);

        var tex = new Texture_t { handle = entry.Texture, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
        var err = OpenVR.Overlay.SetOverlayTexture(overlay, ref tex);
        if (err == EVROverlayError.None) return true;
        Log.Warn($"VR: テクスチャを SteamVR に渡せませんでした ({err})");
        return false;
    }

    public void Dispose()
    {
        foreach (var (texture, _, _) in _textures.Values) Release(texture);
        _textures.Clear();
        if (_context != 0) { Release(_context); _context = 0; }
        if (_device != 0) { Release(_device); _device = 0; }
    }

    private static void* Vtable(nint obj, int index) => (*(void***)obj)[index];

    private static void Release(nint obj) => ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(obj, VtRelease))(obj);

#if DEBUG
    /// <summary>検証用: SteamVR 無しで、デバイスとテクスチャの作成・更新まで通るかを見る (SteamVR に渡す所だけ除く)。</summary>
    internal bool UpdateForTest(byte[] rgba, int width, int height)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = DxgiFormatR8G8B8A8Unorm, SampleCount = 1, BindFlags = BindShaderResource,
        };
        var create = (delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Vtable(_device, VtDeviceCreateTexture2D);
        nint texture;
        if (create(_device, &desc, 0, &texture) < 0) return false;
        var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Vtable(_context, VtContextUpdateSubresource);
        fixed (byte* p = rgba) update(_context, texture, 0, 0, p, (uint)(width * 4), 0);
        ((delegate* unmanaged[Stdcall]<nint, void>)Vtable(_context, VtContextFlush))(_context);
        Release(texture);
        return true;
    }
#endif
}
