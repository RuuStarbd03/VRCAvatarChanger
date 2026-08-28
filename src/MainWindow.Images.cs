using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

// 画像: サムネイルの取得・デコードと、メモリ / ディスクの二段キャッシュ。
// ディスクキャッシュ (ImageDiskCache) があるので、二回目以降の起動では通信せずに一覧が埋まる。
//
// 負荷を抑えるための決めごと:
//   ・デコードは UI スレッドでやらない (1 枚 2〜6ms。数百枚だと操作が引っかかる)
//   ・表示に必要な幅だけデコードする (リスト表示のサムネは小さいので、時間もメモリも大きく減る)
//   ・読む順番は「画面に出たもの優先、残りは 1 本ずつ静かに」。一気に数百件を取りに行かない
//
// 「画面に出てから読む」だけだと、スクロールした先が灰色のままになる瞬間ができる。それを避けるため:
//   ・一覧は画面の前後 1 ページ分まで実体化する (見える前に読み始める)
//   ・実体化されていない残りも、裏で 1 件ずつ最後まで埋める (スクロールバーで飛んでも間に合っている)
public partial class MainWindow
{
    private const int ListThumbWidth = 128;  // リスト表示の行サムネ (実サイズ ~96px、高 DPI でも足りる)
    private const int GridThumbWidth = 320;  // ボックス表示 3 列でも粗くならない幅

    private const int FrontConcurrency = 4;  // 画面に出ているものは並列で急いで読む
    private const int FillConcurrency = 1;   // 残りは 1 本ずつ。VRChat 側にも自分の CPU にも優しく

    private int ThumbWidth => IsGridView ? GridThumbWidth : ListThumbWidth;

    private readonly Queue<AvatarItem> _thumbFront = new();   // 画面に出た (出そうな) もの
    private readonly HashSet<AvatarItem> _thumbFrontSet = []; // 二重投入よけ
    private List<AvatarItem> _thumbFill = [];                 // 残り全部 (表示順)
    private int _thumbFillIndex;
    private int _thumbRunning;
    private CancellationToken _thumbCt;

    /// <summary>まだ今の表示に足りる画像を持っていない (未取得、または小さくデコードしたものしかない)。</summary>
    private bool NeedsThumbnail(AvatarItem item)
        => item.ThumbnailUrl is not null && (item.Thumbnail is null || item.ThumbnailWidth < ThumbWidth);

    /// <summary>一覧が変わったとき / 表示形式が変わったとき、全件を「裏で埋める」対象として積み直す。</summary>
    private void QueueThumbnails(List<AvatarItem> items, CancellationToken ct)
    {
        _thumbCt = ct;
        _thumbFront.Clear();
        _thumbFrontSet.Clear();
        _thumbFill = items.Where(i => i.IsAvatar && i.ThumbnailUrl is not null).ToList();
        _thumbFillIndex = 0;
        PumpThumbnails();
    }

    /// <summary>タイルが画面に出た (実体化された) ときに呼ばれる。他を後回しにして先に読む。</summary>
    private void RequestThumbnail(AvatarItem item)
    {
        // グループタイルは代表メンバーの画像を映しているので、読むのは代表のぶん
        if (item.IsGroup) item = item.Representative!;
        if (!NeedsThumbnail(item) || !_thumbFrontSet.Add(item)) return;
        _thumbFront.Enqueue(item);
        PumpThumbnails();
    }

    /// <summary>データテンプレートの根から。タイルが作られたとき / 別のアバターに使い回されたときに呼ばれる。</summary>
    private void AvatarTile_Realized(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AvatarItem item) RequestThumbnail(item);
    }

    private void AvatarTile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is AvatarItem item) RequestThumbnail(item);
    }

    /// <summary>空いている枠のぶんだけ読み込みを始める。画面に出ているものがある間は、そちらを優先する。</summary>
    private void PumpThumbnails()
    {
        while (true)
        {
            var front = _thumbFront.Count > 0;
            if (_thumbRunning >= (front ? FrontConcurrency : FillConcurrency)) return;
            var item = front ? _thumbFront.Dequeue() : NextFillItem();
            if (item is null) return;
            if (!NeedsThumbnail(item)) { _thumbFrontSet.Remove(item); continue; } // 先に読み終わっていた
            _thumbRunning++;
            _ = LoadThumbnailAsync(item, _thumbCt);
        }
    }

    private AvatarItem? NextFillItem()
    {
        while (_thumbFillIndex < _thumbFill.Count)
        {
            var item = _thumbFill[_thumbFillIndex++];
            if (NeedsThumbnail(item)) return item;
        }
        return null;
    }

    private async Task LoadThumbnailAsync(AvatarItem item, CancellationToken ct)
    {
        try
        {
            var width = ThumbWidth;
            if (await GetImageAsync(item.ThumbnailUrl!, ct) is { } image)
            {
                item.Thumbnail = image;
                item.ThumbnailWidth = width;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _thumbRunning--;
            _thumbFrontSet.Remove(item);
            if (!ct.IsCancellationRequested) PumpThumbnails();
        }
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
