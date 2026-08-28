using System.IO;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

// 画像: サムネイルの取得・デコードと、メモリ / ディスクの二段キャッシュ。
// ディスクキャッシュ (ImageDiskCache) があるので、二回目以降の起動では通信せずに一覧が埋まる。
//
// 負荷を抑えるための決めごとが 2 つある:
//   ・デコードは UI スレッドでやらない (1 枚 2〜6ms。数百枚だと操作が引っかかる)
//   ・表示に必要な幅だけデコードする (リスト表示のサムネは小さいので、時間もメモリも大きく減る)
public partial class MainWindow
{
    private const int ListThumbWidth = 128;  // リスト表示の行サムネ (実サイズ ~46px、高 DPI でも足りる)
    private const int GridThumbWidth = 320;  // ボックス表示 3 列でも粗くならない幅

    private int ThumbWidth => IsGridView ? GridThumbWidth : ListThumbWidth;

    private async Task LoadThumbnailsAsync(List<AvatarItem> items, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Where(i => i.ThumbnailUrl is not null).Select(async item =>
        {
            await gate.WaitAsync(ct);
            try { item.Thumbnail = await GetImageAsync(item.ThumbnailUrl!, ct); }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 今の一覧から参照されなくなった画像をキャッシュから外す(再読み込みで URL が変わった古いサムネなど)。
    /// 長時間の使用でメモリが増え続けないようにするための整理で、タブ切り替え程度では消えない量を上限にしている。
    /// </summary>
    private void PruneImageCache()
    {
        const int maxEntries = 400; // 1 枚 ~300KB (320px) / ~50KB (128px)。捨てても次はディスクから戻る
        if (_imageCache.Count <= maxEntries) return;
        var keep = new HashSet<string>(_allItems.Select(i => i.ThumbnailUrl).OfType<string>());
        if (_user?.CurrentAvatarThumbnailImageUrl is { } header) keep.Add(header);
        foreach (var key in _imageCache.Keys.Where(k => !keep.Contains(UrlOfKey(k))).ToList())
            _imageCache.Remove(key);
    }

    // メモリキャッシュのキー。同じ URL でもデコード幅が違えば別物なので、幅を混ぜる
    private static string CacheKey(string url, int width) => width + "|" + url;

    private static string UrlOfKey(string key) => key[(key.IndexOf('|') + 1)..];

    // 取得中の画像。同じサムネをヘッダと一覧が同時に要求したときに二重ダウンロードしないための台帳
    private readonly Dictionary<string, Task<BitmapImage?>> _imageLoads = [];

    private Task<BitmapImage?> GetImageAsync(string url, CancellationToken ct)
    {
        var width = ThumbWidth;
        if (CachedImage(url, width) is { } cached) return Task.FromResult<BitmapImage?>(cached);
        var key = CacheKey(url, width);
        if (_imageLoads.TryGetValue(key, out var inFlight)) return inFlight;
        var task = DownloadAndDecodeAsync(url, width, key, ct);
        _imageLoads[key] = task;
        return task;
    }

    /// <summary>キャッシュ済みの画像。要求より大きいものが既にあるなら、それを縮めて出せば足りる。</summary>
    private BitmapImage? CachedImage(string url, int width)
    {
        if (_imageCache.TryGetValue(CacheKey(url, width), out var exact)) return exact;
        if (width < GridThumbWidth && _imageCache.TryGetValue(CacheKey(url, GridThumbWidth), out var larger)) return larger;
        return null;
    }

    private async Task<BitmapImage?> DownloadAndDecodeAsync(string url, int width, string key, CancellationToken ct)
    {
        try
        {
            // まずディスクキャッシュを見て、無ければ VRChat から取って保存する
            var bytes = await ImageDiskCache.TryReadAsync(url, ct);
            var fromDisk = bytes is not null;
            bytes ??= await _api.DownloadImageAsync(url, ct); // 失敗・キャンセル時は null (例外は投げない)
            var img = bytes is null ? null : await DecodeAsync(bytes, width, ct);
            if (img is null && fromDisk)
            {
                // キャッシュが壊れていた: 捨てて取り直す
                ImageDiskCache.Delete(url);
                fromDisk = false;
                bytes = await _api.DownloadImageAsync(url, ct);
                img = bytes is null ? null : await DecodeAsync(bytes, width, ct);
            }
            if (img is null) return null;
            if (!fromDisk) _ = ImageDiskCache.WriteAsync(url, bytes!);
            _imageCache[key] = img;
            return img;
        }
        catch (OperationCanceledException) { return null; }
        finally { _imageLoads.Remove(key); } // 失敗・キャンセル分を台帳に残さない(次の要求で再試行できる)
    }

    /// <summary>
    /// 受信したバイト列を表示用のビットマップにする。壊れていれば null。
    /// デコードは 1 枚あたり数ミリ秒かかるので、UI スレッドから外して行う
    /// (Freeze 済みなので、出来上がったものは UI スレッドでそのまま使える)。
    /// </summary>
    private static Task<BitmapImage?> DecodeAsync(byte[] bytes, int width, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                var img = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.DecodePixelWidth = width;
                    img.StreamSource = ms;
                    img.EndInit();
                }
                img.Freeze();
                return (BitmapImage?)img;
            }
            catch { return null; }
        }, ct);
}
