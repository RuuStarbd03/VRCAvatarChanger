using System.IO;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

// 画像: サムネイルの取得・デコードと、メモリ / ディスクの二段キャッシュ。
// ディスクキャッシュ (ImageDiskCache) があるので、二回目以降の起動では通信せずに一覧が埋まる。
public partial class MainWindow
{
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
        const int maxEntries = 600; // 320px デコードで 1 枚 ~300KB。600 枚 ≒ 180MB を上限の目安に
        if (_imageCache.Count <= maxEntries) return;
        var keep = new HashSet<string>(_allItems.Select(i => i.ThumbnailUrl).OfType<string>());
        if (_user?.CurrentAvatarThumbnailImageUrl is { } header) keep.Add(header);
        foreach (var url in _imageCache.Keys.Where(u => !keep.Contains(u)).ToList())
            _imageCache.Remove(url);
    }

    // ダウンロード中の URL。同じサムネをヘッダと一覧が同時に要求したときに二重ダウンロードしないための台帳
    private readonly Dictionary<string, Task<BitmapImage?>> _imageLoads = [];

    private Task<BitmapImage?> GetImageAsync(string url, CancellationToken ct)
    {
        if (_imageCache.TryGetValue(url, out var cached)) return Task.FromResult<BitmapImage?>(cached);
        if (_imageLoads.TryGetValue(url, out var inFlight)) return inFlight;
        var task = DownloadAndDecodeAsync(url, ct);
        _imageLoads[url] = task;
        return task;
    }

    private async Task<BitmapImage?> DownloadAndDecodeAsync(string url, CancellationToken ct)
    {
        try
        {
            // まずディスクキャッシュを見て、無ければ VRChat から取って保存する
            var bytes = await ImageDiskCache.TryReadAsync(url, ct);
            var fromDisk = bytes is not null;
            bytes ??= await _api.DownloadImageAsync(url, ct); // 失敗・キャンセル時は null (例外は投げない)
            var img = bytes is null ? null : Decode(bytes);
            if (img is null && fromDisk)
            {
                // キャッシュが壊れていた: 捨てて取り直す
                ImageDiskCache.Delete(url);
                fromDisk = false;
                bytes = await _api.DownloadImageAsync(url, ct);
                img = bytes is null ? null : Decode(bytes);
            }
            if (img is null) return null;
            if (!fromDisk) _ = ImageDiskCache.WriteAsync(url, bytes!);
            _imageCache[url] = img;
            return img;
        }
        finally { _imageLoads.Remove(url); } // 失敗・キャンセル分を台帳に残さない(次の要求で再試行できる)
    }

    /// <summary>受信したバイト列を表示用のビットマップにする。壊れていれば null。</summary>
    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 320; // ボックス表示 3 列でも粗くならない幅
                img.StreamSource = ms;
                img.EndInit();
            }
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
}
