using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VRCAvatarChanger;

/// <summary>
/// VRChat との OSC 連携 (すべて 127.0.0.1 のローカル通信のみ)。
/// 受信 (既定 9001): VRChat からの /avatar/change を検知して通知する。
/// 送信 (既定 9000): /avatar/change を VRChat に送り、ローカルでアバターを切り替えさせる。
/// 依存ライブラリなしの最小限 OSC パーサー(メッセージ / バンドル対応)。
/// </summary>
public sealed class OscListener : IDisposable
{
    public const int DefaultPort = 9001;
    /// <summary>VRChat の OSC 入力ポート(アプリ → ゲーム方向)。</summary>
    public const int GameInPort = 9000;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private long _lastReceiveTicks; // 受信スレッドが書き、UI スレッドが読む (UTC ticks)

    /// <summary>/avatar/change を受信したとき。引数はアバター ID (avtr_...)。UI スレッド外から呼ばれる。</summary>
    public event Action<string>? AvatarChanged;

    public int Port { get; private set; }
    public bool IsListening => _udp is not null;

    /// <summary>
    /// VRChat が OSC を送ってきているか(直近 10 秒以内に何か受信したか)。
    /// アプリ → ゲームの送信が届く見込みがあるかの判定に使う。
    /// </summary>
    public bool IsGameConnected => IsListening &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastReceiveTicks) < TimeSpan.FromSeconds(10).Ticks;

    /// <summary>待ち受け開始。ポートが使用中などで失敗したら例外。</summary>
    public void Start(int port = DefaultPort)
    {
        Stop();
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _udp = udp;
        Port = port;
        _cts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(udp, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _udp?.Dispose();
        _udp = null;
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(ct);
                Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);
                try { HandlePacket(result.Buffer); } catch { /* 壊れたパケットは無視 */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private void HandlePacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return;
        if (data.StartsWith("#bundle\0"u8))
        {
            // "#bundle\0" (8) + timetag (8) + [size(4) + element]...
            var pos = 16;
            while (pos + 4 <= data.Length)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(data[pos..]);
                pos += 4;
                if (size < 0 || pos + size > data.Length) break;
                HandlePacket(data.Slice(pos, size));
                pos += size;
            }
            return;
        }

        var p = 0;
        var address = ReadOscString(data, ref p);
        if (address != "/avatar/change") return;
        var typeTag = ReadOscString(data, ref p);
        if (typeTag.Length < 2 || typeTag[1] != 's') return;
        var avatarId = ReadOscString(data, ref p);
        if (avatarId.StartsWith("avtr_", StringComparison.Ordinal)) AvatarChanged?.Invoke(avatarId);
    }

    /// <summary>/avatar/change を VRChat (127.0.0.1:9000) に送る。宛先はローカルのみ。失敗したら false。</summary>
    public bool SendAvatarChange(string avatarId)
    {
        try
        {
            var msg = BuildMessage("/avatar/change", avatarId);
            using var udp = new UdpClient();
            udp.Send(msg, msg.Length, new IPEndPoint(IPAddress.Loopback, GameInPort));
            return true;
        }
        catch { return false; }
    }

    private static byte[] BuildMessage(string address, string stringArg)
    {
        // OSC 文字列: UTF-8 + NUL 終端 + 4 バイト境界までパディング (ReadOscString と対になる形式)
        static byte[] OscString(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var r = new byte[(b.Length + 4) & ~3];
            b.CopyTo(r, 0);
            return r;
        }
        return [.. OscString(address), .. OscString(",s"), .. OscString(stringArg)];
    }

    private static string ReadOscString(ReadOnlySpan<byte> data, ref int pos)
    {
        var end = data[pos..].IndexOf((byte)0);
        if (end < 0) end = data.Length - pos;
        var s = Encoding.UTF8.GetString(data.Slice(pos, end));
        // 4 バイト境界にパディング(終端 NUL を含む)
        pos += (end + 4) & ~3;
        return s;
    }

    public void Dispose() => Stop();
}
