namespace VRCAvatarChanger;

// ダウンロードサイズの取得。
//
// サイズはアバター一覧の応答に入っておらず、ファイルの情報を 1 件 1 リクエストで引く必要がある。
// 全部まとめて取ると数百リクエストになりレート制限に当たるので:
//   ・パフォーマンス順で見ているときだけ取りに行く (表示に使うのがそこだけのため)
//   ・画面に出たタイルから 1 件ずつ、間隔を空けて取る
//   ・一度引いたらディスクに残す (同じバージョンならサイズは変わらない)
public partial class MainWindow
{
    private readonly Queue<AvatarItem> _sizeQueue = new();
    private readonly HashSet<string> _sizeQueued = [];
    private bool _sizePumping;

    /// <summary>サイズを表示する並びか (取りに行くのはこのときだけ)。</summary>
    private bool SizeWanted => SortKey.StartsWith("performance", StringComparison.Ordinal);

    /// <summary>タイルが画面に出たときに呼ぶ。まだサイズを知らなければ取得待ちに積む。</summary>
    private void RequestAssetSize(AvatarItem item)
    {
        if (_preview || item.IsGroup || !SizeWanted) return;
        if (item.Avatar.WindowsAssetRef is not { } r) return;
        if (AssetSizeCache.TryGet(r.FileId, r.Version, out _)) return;
        if (!_sizeQueued.Add(item.Id)) return;
        _sizeQueue.Enqueue(item);
        _ = PumpSizesAsync();
    }

    /// <summary>
    /// 画面に出ているサムネイルが読み終わるまで待つ。サイズとサムネイルは同じ通信と
    /// レート制限を共有していて、サイズを挟むとサムネイルが遅れたり 429 で落ちたりする。
    /// サムネイルのほうが先に見たいものなので、そちらを待たせない。
    /// 待っている間に並びが変わったら false を返す (もう取りに行かなくてよい)。
    /// </summary>
    private async Task<bool> WaitForThumbnailsAsync()
    {
        while (_thumbFront.Count > 0 || _thumbRunning > 0)
        {
            if (!SizeWanted) return false;
            await Task.Delay(300);
        }
        return SizeWanted;
    }

    /// <summary>1 件ずつ順に引く。並列にしないのは、1 件 1 リクエストで数が多いため。</summary>
    private async Task PumpSizesAsync()
    {
        if (_sizePumping) return;
        _sizePumping = true;
        try
        {
            while (_sizeQueue.Count > 0)
            {
                var item = _sizeQueue.Dequeue();
                _sizeQueued.Remove(item.Id);
                // 待っている間に別の並びへ移っていたら、そこで止める (見ていないもののために叩かない)
                if (!SizeWanted) { _sizeQueue.Clear(); _sizeQueued.Clear(); break; }
                if (item.Avatar.WindowsAssetRef is not { } r) continue;

                if (!await WaitForThumbnailsAsync()) break;
                var size = await _api.GetAssetSizeAsync(r.FileId, r.Version);
                if (size is { } s)
                {
                    AssetSizeCache.Set(r.FileId, r.Version, s);
                    if (SizeWanted) ApplyPerformanceText(item);
                }
                // 取れなかったときはレート制限に当たっている可能性が高いので、長めに空ける
                await Task.Delay(size is null ? 3000 : 600);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 取れなくてもランクだけ出せばよい */ }
        finally
        {
            _sizePumping = false;
            AssetSizeCache.Flush();
        }
    }

    /// <summary>
    /// パフォーマンス順のときの表示を入れる。サイズとランクは 1 行にすると狭い列で
    /// 「Very Poor」が切れてしまうので、行を分けて両方読めるようにする。
    /// </summary>
    private static void ApplyPerformanceText(AvatarItem item)
    {
        var rank = PerformanceLabel(item.Avatar.Performance?.Windows);
        var size = item.Avatar.WindowsAssetRef is { } r && AssetSizeCache.TryGet(r.FileId, r.Version, out var b)
            ? AssetSizeCache.Format(b)
            : null;
        // サイズがまだ分からないうちはランクだけを 2 行目に出す (行が増えたり減ったりしないように)
        item.SubText = size ?? rank ?? item.AuthorName;
        item.SubText2 = size is not null ? rank : null;
    }
}
