using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

// 画像: サムネイルの取得・展開と、ディスクキャッシュ (ImageDiskCache) との行き来。
//
// 開きっぱなしで使う前提なので、メモリが増え続けないことを最優先にしている:
//   ・展開した画像を持つのは AvatarItem だけ。合計サイズに上限を設け、
//     最後に画面へ出たのが古いものから手放す (捨ててもディスクから数ミリ秒で戻せる)
//   ・裏で先に用意するのは「ファイルをディスクに置くところまで」で、展開はしない。
//     見てもいない数百枚を展開すると、それだけで数十 MB になるため
//   ・展開するのは画面に出た (出そうな) ものだけ。1 枚あたり数ミリ秒かかるので UI スレッドの外で行う
//
// 「画面に出てから読む」だけだとスクロールした先が灰色になるので、一覧は画面の前後 1 ページ分
// (ボックス表示は前後 2 行) まで実体化しておき、見える前に展開を始める。
public partial class MainWindow
{
    private const int ListThumbWidth = 128;  // リスト表示の行サムネ (実サイズ 96 DIP。高 DPI でも足りる)
    private const int FrontConcurrency = 4;  // 画面に出ているものは並列で急いで展開する

    /// <summary>展開したまま抱えておく画像の合計上限。1 枚 110KB (192px) なら約 440 枚ぶん。</summary>
    private const long MaxThumbnailBytes = 48L * 1024 * 1024;

    /// <summary>
    /// 展開する幅の段階。表示する大きさに合わせて選ぶ (小さすぎるとぼやけ、大きすぎるとメモリの無駄)。
    /// 1px 刻みにするとウィンドウを動かすたびに展開し直すことになるので、決まった段階に丸める。
    /// 上限を 320px にしているのは、VRChat のサムネイル自体がそれほど大きくないため
    /// (それ以上を指定しても引き伸ばすだけでメモリを食う)。
    /// </summary>
    private static readonly int[] ThumbWidthSteps = [128, 192, 256, 320];

    /// <summary>
    /// 今の表示で 1 枚に必要な展開幅。ボックス表示はタイルの実寸 (一覧の幅 ÷ 列数) に合わせるので、
    /// 列数を増やして 1 枚が小さくなるほど、抱えるメモリも小さくなる。
    /// </summary>
    private int ThumbWidth
    {
        get
        {
            var dip = IsGridView && GridColumns > 0 && AvatarList.ActualWidth > 0
                ? AvatarList.ActualWidth / GridColumns
                : 96; // リスト表示の行サムネは 96 DIP 固定
            var needed = dip * VisualTreeHelper.GetDpi(this).DpiScaleX; // 高 DPI ではその分だけ実ピクセルが要る
            foreach (var step in ThumbWidthSteps)
                if (step >= needed) return step;
            return ThumbWidthSteps[^1];
        }
    }

    // 展開待ち (画面に出た / 出そうなもの)
    private readonly Queue<AvatarItem> _thumbFront = new();
    private readonly HashSet<AvatarItem> _thumbFrontSet = [];
    private int _thumbRunning;
    private CancellationToken _thumbCt;

    /// <summary>取れなかったサムネイルの再試行回数。上限を超えたらあきらめる (消された画像を叩き続けないため)。</summary>
    private readonly Dictionary<AvatarItem, int> _thumbAttempts = [];
    private const int MaxThumbnailAttempts = 3;
#if DEBUG
    /// <summary>検証用: VRCAC_TEST_FAIL_FIRST=n で最初の n 枚の取得を失敗させ、レート制限に当たった状態を作る。</summary>
    private int _testFailFirst = int.TryParse(Environment.GetEnvironmentVariable("VRCAC_TEST_FAIL_FIRST"), out var f) ? f : 0;
#endif

    // 展開済み画像の管理。合計サイズと「最後に画面へ出た順」(先頭が新しい)
    private long _thumbBytes;
    private readonly LinkedList<AvatarItem> _thumbLru = new();
    private readonly Dictionary<AvatarItem, LinkedListNode<AvatarItem>> _thumbLruNodes = [];

    // 取得中の画像。同じサムネを一覧とヘッダが同時に要求したときに二重取得しないための台帳
    private readonly Dictionary<string, Task<BitmapImage?>> _imageLoads = [];

    // 裏でディスクキャッシュを用意する処理
    private List<AvatarItem> _thumbItems = [];
    private CancellationTokenSource? _warmCts;

    /// <summary>今の表示に足りる画像を持っていない (未展開、または小さく展開したものしかない)。</summary>
    private bool NeedsThumbnail(AvatarItem item)
        => item.ThumbnailUrl is not null && (item.Thumbnail is null || item.ThumbnailWidth < ThumbWidth);

    /// <summary>一覧が変わったとき / 表示形式が変わったときに呼ぶ。</summary>
    private void QueueThumbnails(List<AvatarItem> items, CancellationToken ct)
    {
        _thumbCt = ct;
        _thumbAttempts.Clear(); // 一覧が変わったら再試行の回数は数え直す
        _thumbItems = items;
        // ここへ来る前にタイルが実体化していると、その要求はもう入っている。
        // 一律に捨てると「画面に出ているのに要求だけ消えた」タイルができ、
        // 要求はタイルが出た瞬間にしか発行されないので二度と埋まらない。今の一覧に残るものは持ち越す
        var keep = new HashSet<AvatarItem>(items);
        var pending = _thumbFront.Where(keep.Contains).ToList();
        _thumbFront.Clear();
        _thumbFrontSet.Clear();
        foreach (var item in pending)
            if (_thumbFrontSet.Add(item)) _thumbFront.Enqueue(item);
        RebuildThumbnailBudget(items);
        StartWarming(ct);
        PumpThumbnails();
    }

    /// <summary>タイルが画面に出た (実体化された) ときに呼ばれる。他を後回しにして先に展開する。</summary>
    private void RequestThumbnail(AvatarItem item)
    {
        RequestAssetSize(item); // 出たタイルから順に、必要ならダウンロードサイズも取りに行く
        // グループタイルは代表メンバーの画像を映しているので、扱うのは代表のぶん
        if (item.IsGroup) item = item.Representative!;
        TouchThumbnail(item); // 「今見えている」印。手放す順番の基準になる
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

    private void PumpThumbnails()
    {
        while (_thumbFront.Count > 0 && _thumbRunning < FrontConcurrency)
        {
            var item = _thumbFront.Dequeue();
            _thumbFrontSet.Remove(item);
            if (!NeedsThumbnail(item)) continue; // 先に読み終わっていた
            _thumbRunning++;
            _ = LoadThumbnailAsync(item, _thumbCt);
        }
    }

    private async Task LoadThumbnailAsync(AvatarItem item, CancellationToken ct)
    {
        var loaded = false;
        try
        {
            var width = ThumbWidth;
#if DEBUG
            // 429 と同じく「取れなかった」状態にする (return してしまうと再試行の経路まで飛ばすので、値で分ける)
            var forceFail = _testFailFirst > 0;
            if (forceFail) _testFailFirst--;
#else
            const bool forceFail = false;
#endif
            if (!forceFail && await GetImageAsync(item.ThumbnailUrl!, width, ct) is { } image)
            {
                SetThumbnail(item, image, width);
                loaded = true;
                _thumbAttempts.Remove(item);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _thumbRunning--;
            if (!ct.IsCancellationRequested) PumpThumbnails();
        }
        // 取れなかったぶんは時間を置いて取り直す。要求はタイルが画面に出た瞬間しか出ないので、
        // ここで拾わないと「画面に出たままのタイルが空のまま」になる (レート制限に当たったときに起きる)
        if (!loaded && !ct.IsCancellationRequested) ScheduleThumbnailRetry(item, ct);
    }

    /// <summary>
    /// 取れなかったサムネイルを、間を置いて取り直す。
    /// 消えた画像を延々と叩かないよう回数に上限を設け、待ち時間は 1 回ごとに延ばす。
    /// </summary>
    private async void ScheduleThumbnailRetry(AvatarItem item, CancellationToken ct)
    {
        var tried = _thumbAttempts.TryGetValue(item, out var n) ? n : 0;
        if (tried >= MaxThumbnailAttempts) return;
        _thumbAttempts[item] = tried + 1;
        try { await Task.Delay(TimeSpan.FromSeconds(3 * (tried + 1)), ct); }
        catch (OperationCanceledException) { return; }
        // 待っている間に一覧が入れ替わった / 別経路で読めていたなら何もしない
        if (!NeedsThumbnail(item) || !_thumbItems.Contains(item)) return;
        if (_thumbFrontSet.Add(item)) _thumbFront.Enqueue(item);
        PumpThumbnails();
    }

    // ---------------- 展開済み画像の上限 ----------------

    private static long BytesOf(BitmapImage image) => (long)image.PixelWidth * image.PixelHeight * 4;

    private void SetThumbnail(AvatarItem item, BitmapImage image, int width)
    {
        if (item.Thumbnail is { } old) _thumbBytes -= BytesOf(old);
        item.Thumbnail = image;
        item.ThumbnailWidth = width;
        _thumbBytes += BytesOf(image);
        TouchThumbnail(item);
        TrimThumbnails();
    }

    /// <summary>「最後に画面へ出た順」の先頭に持ってくる。</summary>
    private void TouchThumbnail(AvatarItem item)
    {
        if (item.Thumbnail is null) return;
        if (_thumbLruNodes.TryGetValue(item, out var node)) _thumbLru.Remove(node);
        else node = new LinkedListNode<AvatarItem>(item);
        _thumbLru.AddFirst(node);
        _thumbLruNodes[item] = node;
    }

    /// <summary>
    /// 合計が上限を超えたら、最後に画面へ出たのが古いものから画像を手放す。
    /// 今見えているものは直前に印が付いているので対象にならない。
    /// </summary>
    private void TrimThumbnails()
    {
        var node = _thumbLru.Last;
        while (node is not null && _thumbBytes > MaxThumbnailBytes)
        {
            var prev = node.Previous;
            var item = node.Value;
            if (item.Thumbnail is { } image)
            {
                _thumbBytes -= BytesOf(image);
                item.Thumbnail = null;
                item.ThumbnailWidth = 0;
            }
            _thumbLru.Remove(node);
            _thumbLruNodes.Remove(item);
            node = prev;
        }
    }

    /// <summary>一覧が入れ替わったときに、合計と順番を今の顔ぶれで作り直す。</summary>
    private void RebuildThumbnailBudget(List<AvatarItem> items)
    {
        _thumbLru.Clear();
        _thumbLruNodes.Clear();
        _thumbBytes = 0;
        foreach (var item in items)
        {
            if (item.Thumbnail is not { } image) continue;
            _thumbBytes += BytesOf(image);
            _thumbLruNodes[item] = _thumbLru.AddLast(item);
        }
        TrimThumbnails();
    }

    // ---------------- 裏でディスクキャッシュを用意する ----------------

    /// <summary>
    /// まだディスクに無いサムネイルを、裏で 1 件ずつ取っておく (展開はしない = メモリを使わない)。
    /// これがあるので、スクロールした先でも通信待ちにはならず、数ミリ秒の展開だけで出せる。
    /// </summary>
    private void StartWarming(CancellationToken ct)
    {
        _warmCts?.Cancel();
        _warmCts?.Dispose();
        _warmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = WarmDiskCacheAsync(_warmCts.Token);
    }

    /// <summary>トレイから開き直したときなど、止めていた用意を再開する。</summary>
    private void ResumeWarming()
    {
        if (_thumbItems.Count > 0) StartWarming(_thumbCt);
    }

    private async Task WarmDiskCacheAsync(CancellationToken ct)
    {
        try
        {
            if (!IsVisible) return; // 誰も見ていない間は進めない
            var urls = _thumbItems.Where(i => i.IsAvatar).Select(i => i.ThumbnailUrl).OfType<string>().Distinct().ToList();
            foreach (var url in await ImageDiskCache.MissingAsync(urls, ct))
            {
                // 画面に出ているものの展開を邪魔しない。閉じられたらそこで止める
                while (_thumbFront.Count > 0 || _thumbRunning > 0)
                {
                    await Task.Delay(50, ct);
                    if (!IsVisible) return;
                }
                if (!IsVisible) return;
                if (await _api.DownloadImageAsync(url, ct) is { } bytes) await ImageDiskCache.WriteAsync(url, bytes);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------------- 取得と展開 ----------------

    private Task<BitmapImage?> GetImageAsync(string url, int width, CancellationToken ct)
    {
        var key = width + "|" + url;
        if (_imageLoads.TryGetValue(key, out var inFlight)) return inFlight;
        var task = LoadImageAsync(url, width, key, ct);
        _imageLoads[key] = task;
        return task;
    }

    private async Task<BitmapImage?> LoadImageAsync(string url, int width, string key, CancellationToken ct)
    {
        try
        {
            // ディスクにあればそこから展開する。無ければ VRChat から取って保存する
            if (await DecodeFromDiskAsync(url, width, ct) is { } cached) return cached;
            var bytes = await _api.DownloadImageAsync(url, ct); // 失敗・キャンセル時は null (例外は投げない)
            if (bytes is null) return null;
            var image = await DecodeAsync(bytes, width, ct);
            if (image is null) return null;
            _ = ImageDiskCache.WriteAsync(url, bytes);
            return image;
        }
        catch (OperationCanceledException) { return null; }
        finally { _imageLoads.Remove(key); } // 失敗・キャンセル分を台帳に残さない(次の要求で再試行できる)
    }

    /// <summary>ディスクキャッシュのファイルから直接展開する。無い / 壊れていれば null。</summary>
    private static Task<BitmapImage?> DecodeFromDiskAsync(string url, int width, CancellationToken ct)
        => Task.Run(() =>
        {
            using var stream = ImageDiskCache.TryOpen(url);
            if (stream is null) return null;
            var image = Decode(stream, width);
            if (image is null) ImageDiskCache.Delete(url); // 壊れていたので捨てて取り直させる
            return image;
        }, ct);

    private static Task<BitmapImage?> DecodeAsync(byte[] bytes, int width, CancellationToken ct)
        => Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes);
            return Decode(stream, width);
        }, ct);

    /// <summary>
    /// 表示用のビットマップにする。壊れていれば null。
    /// 展開は 1 枚あたり数ミリ秒かかるので UI スレッドの外から呼ぶこと
    /// (Freeze 済みなので、出来上がったものは UI スレッドでそのまま使える)。
    /// </summary>
    private static BitmapImage? Decode(Stream stream, int width)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // ここで読み切るので、あとでストリームを閉じてよい
            image.DecodePixelWidth = width;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
