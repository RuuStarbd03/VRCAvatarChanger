using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

/// <summary>
/// VR (SteamVR) のアバター選択パネルと手首ボタンの見た目・当たり判定。
/// SteamVR の画面には WPF をそのまま出せないので、ここで 1 枚の画像に描いて渡す。
/// 描画と当たり判定だけを受け持ち、SteamVR には触らない (VrOverlay.cs が繋ぐ)。
/// そのため SteamVR の無い PC でも、プレビューで見た目と押した場所の判定を確かめられる。
///
/// PC の画面を触らずに VR の中だけで探せるよう、タブ (自分 / お気に入り / パブリック)・並び順・
/// グループ化・絞り込み (お気に入りグループ / タグ) をパネルの中で切り替えられる。
/// 文字入力や細かいスクロールはしにくいので、検索は置かず、タイルをページ送りで見せる。
/// 列数 (3〜10) もパネルの中で変えられる。段の数は列数から、パネルに収まるだけ決まる。
///
/// ホバーやサムネイルの到着のたびに描き直すので、描画先の画像と SteamVR に渡す配列 (2MB) は使い回す
/// (毎回作ると大きなオブジェクトとして積み上がり、GC の負担になる)。
/// </summary>
internal sealed class VrPanel
{
    // 画像の大きさ (px)。SteamVR に渡す生データの量を抑えつつ、0.5m 幅で文字が読める程度
    public const int Width = 640;
    public const int Height = 800;
    public const int ButtonSize = 192;
    public const int CursorSize = 64;

    public const int MinColumns = Settings.MinGridColumns;
    public const int MaxColumns = Settings.MaxGridColumns;

    /// <summary>タブの値と表示名 (並びは画面の左から)。</summary>
    public static readonly string[] Tabs = [AvatarTab.Own, AvatarTab.Favorites, AvatarTab.Public];
    private static readonly string[] TabLabels = ["自分", "お気に入り", "パブリック"];

    // ---- レイアウト (px) ----
    private const double Pad = 28;
    // 1 段目: タブ + 閉じる
    private static readonly Rect TabsRect = new(Pad, 20, Width - Pad * 2 - 64, 48);
    private static readonly Point CloseCenter = new(Width - Pad - 24, 44);
    private const double CloseRadius = 24;
    // 2 段目: 並び順 + グループ化
    private static readonly Rect SortRecentRect = new(Pad, 80, 140, 44);
    private static readonly Rect SortNameRect = new(Pad + 146, 80, 96, 44);
    private static readonly Rect ColumnsLessRect = new(282, 82, 40, 40);
    private static readonly Rect ColumnsMoreRect = new(390, 82, 40, 40);
    private static readonly Rect GroupToggleRect = new(Width - Pad - 170, 80, 170, 44);
    // 3 段目: 絞り込みのチップ (グループを開いているときは「戻る」)
    private const double ChipTop = 134;
    private const double ChipHeight = 40;
    private const double ChipGap = 8;
    private const double ChipArrowWidth = 40;
    private static readonly Rect BackRect = new(Pad, ChipTop, 110, ChipHeight);
    // タイル (大きさは列数で決まる: GridLayout)
    private const double GridTop = 186;
    // 下: 状態の一言 + ページ送り
    private const double StatusY = 722;
    private const double GridBottom = StatusY - 14;
    private static readonly Rect PrevRect = new(Pad, 748, 120, 44);
    private static readonly Rect NextRect = new(Width - Pad - 120, 748, 120, 44);

    // 配色 (VR では周りが暗いことが多いので、テーマに関係なく暗い配色で描く)
    private static readonly Brush CardBrush = Frozen(Color.FromArgb(0xF2, 0x1B, 0x1C, 0x21));
    private static readonly Brush TileBrush = Frozen(Color.FromRgb(0x2A, 0x2C, 0x33));
    private static readonly Brush TileHoverBrush = Frozen(Color.FromRgb(0x38, 0x3B, 0x46));
    private static readonly Brush ThumbBackBrush = Frozen(Color.FromRgb(0x22, 0x23, 0x29));
    private static readonly Brush BadgeBrush = Frozen(Color.FromArgb(0xCC, 0x14, 0x14, 0x16));
    private static readonly Brush DimBrush = Frozen(Color.FromArgb(0x99, 0x14, 0x14, 0x16));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF5));
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0xA0, 0xA3, 0xAD));
    // アクセント色はアプリの設定 (Windows に合わせる / 選んだ色) に従う。VR パネルは常に暗い配色なので、ダーク用の色を使う
    private static Color _accentColor;
    private static Brush AccentBrush = Brushes.Transparent;
    // アクセントの上の文字色 (明るいアクセントなら黒、暗ければ白)
    private static Brush OnAccentBrush = Brushes.White;
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(0xE5, 0x6B, 0x6F));
    private static Pen AccentPen = new();
    private static Pen AccentThinPen = new(); // 小さいタイルの「今着ている」枠
    private static readonly Pen SelectedPen = FrozenPen(TextBrush, 4);      // 1 回押して選んだタイルの枠
    private static readonly Pen SelectedThinPen = FrozenPen(TextBrush, 2.5);
    private static readonly Pen EdgePen =FrozenPen(Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 2);
    // 手首ボタン: 塗りつぶさず、暗い半透明の下地に細い輪だけ色を付ける (腕時計の横で主張しすぎないように)
    private static readonly Brush ButtonBackBrush = Frozen(Color.FromArgb(0xD9, 0x1B, 0x1C, 0x21));
    private static Pen ButtonRingPen = new();
    private static readonly Pen ButtonRingOpenPen = FrozenPen(MutedBrush, 6);

    private static readonly FontFamily UiFont = new("Segoe UI Variable Text, Yu Gothic UI, Segoe UI");
    private static readonly Typeface UiFace = new(UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBoldFace = new(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // Segoe Fluent Icons
    private const string GlyphClose = "";
    private const string GlyphPerson = "";
    private const string GlyphCheck = "";
    private const string GlyphLeft = "";
    private const string GlyphRight = "";
    private const string GlyphMinus = "\uE738";
    private const string GlyphPlus = "\uE710";

    /// <summary>押された場所の意味。Index はタブ / チップ / タイルの番号 (タイルは一覧全体での番号)。</summary>
    public enum HitKind { None, Close, Tab, SortRecent, SortName, ColumnsLess, ColumnsMore, GroupToggle, Chip, ChipPrev, ChipNext, Back, Tile, Prev, Next }

    public readonly record struct Hit(HitKind Kind, int Index = -1);

    // ---- 表示する状態 (切り替えの判断は VrOverlay が行い、ここは描くだけ) ----
    public string Tab { get; set; } = AvatarTab.Own;
    public string SortKey { get; set; } = "recent_asc";
    public bool GroupView { get; set; } = true;
    /// <summary>選んでいる絞り込み (null = すべて)。</summary>
    public string? Filter
    {
        get => _filter;
        set { _filter = value; _chipLayout = null; }
    }
    private string? _filter;
    /// <summary>開いているグループの名前 (null = 開いていない)。</summary>
    public string? OpenGroupName { get; set; }
    public string Status { get; set; } = "";
    public bool StatusIsError { get; set; }
    public Hit Hover { get; set; }
    /// <summary>
    /// 1 回押して選んだタイル (一覧全体での番号、-1 = なし)。同じタイルをもう一度押すとチェンジする
    /// (PC のダブルクリックと同じく、うっかり触れただけで着替えないように)。
    /// </summary>
    public int Selected { get; set; } = -1;

    private List<AvatarItem> _items = [];
    private List<string> _chips = [];
    public IReadOnlyList<AvatarItem> Items => _items;
    public IReadOnlyList<string> Chips => _chips;
    public int Page { get; private set; }

    /// <summary>
    /// 列数で決まるタイルの並べ方。3 列で 3 段 (大きいタイル)、10 列では 13 段 (サムネイルだけの小さいタイル)。
    /// 名前は読める大きさのときだけ載せる (小さいときはホバーで下に出す)。
    /// </summary>
    private readonly record struct GridLayout(int Columns, int Rows, double Gap, double TileWidth, double ThumbHeight, double TileHeight,
        double NameSize, bool ShowName)
    {
        /// <summary>3 列のときのタイル幅。文字やバッジの大きさは、これとの比で縮める。</summary>
        private const double BaseWidth = 188;
        public double Scale => TileWidth / BaseWidth;
        public int PageSize => Columns * Rows;

        public static GridLayout For(int columns)
        {
            var gap = columns <= 4 ? 10 : columns <= 6 ? 8 : 6;
            var w = (Width - Pad * 2 - gap * (columns - 1)) / columns;
            var thumb = Math.Round(w * 0.68);
            var nameSize = Math.Clamp(w * 17 / BaseWidth, 11, 17);
            var showName = w >= 70;
            var nameHeight = showName ? Math.Round(nameSize * 2.24) : 0;
            var h = thumb + nameHeight;
            var fit = (GridBottom - GridTop + gap) / (h + gap);
            var rows = Math.Max(1, (int)fit);
            // あと少しでもう 1 段入るなら、サムネイルを少し低くして詰める (下に大きな空きを残さない)
            var squeezed = Math.Floor((GridBottom - GridTop + gap) / (rows + 1) - gap - nameHeight);
            if (fit - rows >= 0.5 && squeezed >= w * 0.6)
            {
                rows++;
                thumb = squeezed;
                h = thumb + nameHeight;
            }
            return new GridLayout(columns, rows, gap, w, thumb, h, nameSize, showName);
        }
    }

    private GridLayout _grid = GridLayout.For(MinColumns);

    /// <summary>1 ページに並ぶ数の最大 (10 列のとき)。描いたサムネイルを覚えておく配列の大きさに使う。</summary>
    public static readonly int MaxPageSize = GridLayout.For(MaxColumns).PageSize;

    public int Columns => _grid.Columns;
    public int PageSize => _grid.PageSize;

    /// <summary>列数を変える。今のページの先頭に見えていたタイルが、変えたあとも見えるページへ移る。</summary>
    public void SetColumns(int columns)
    {
        columns = Math.Clamp(columns, MinColumns, MaxColumns);
        if (columns == _grid.Columns) return;
        var first = PageStart;
        _grid = GridLayout.For(columns);
        Page = Math.Clamp(first / PageSize, 0, PageCount - 1);
        Hover = default;
        Selected = -1;
    }

    public int PageCount => Math.Max(1, (_items.Count + PageSize - 1) / PageSize);
    /// <summary>チップが 1 行に収まらないとき、何番目のチップから見せているか (0 = 「すべて」)。</summary>
    public int ChipStart { get; private set; }

    /// <summary>今のページの先頭が一覧の何番目か。</summary>
    public int PageStart => Page * PageSize;

    /// <summary>今のページに並んでいる数 (最後のページは 1 ページぶんに満たないことがある)。</summary>
    public int PageItemCount => Math.Clamp(_items.Count - PageStart, 0, PageSize);

    /// <summary>今のページの slot 番目 (左上から 0)。</summary>
    public AvatarItem PageItem(int slot) => _items[PageStart + slot];

    /// <summary>今のページに並んでいる項目。</summary>
    public IEnumerable<AvatarItem> PageItems => _items.Skip(PageStart).Take(PageSize);

    // 使い回す描画先と、SteamVR に渡す RGBA の配列
    private readonly RenderTargetBitmap _target = new(Width, Height, 96, 96, PixelFormats.Pbgra32);
    private readonly byte[] _rgba = new byte[Width * Height * 4];

    /// <summary>
    /// 並べるタイルを入れ替える。resetPage なら今着ているアバター (を含むグループ) のページから、
    /// そうでなければ今のページのまま (はみ出したら最後のページ) 見せる。
    /// </summary>
    public void SetItems(List<AvatarItem> tiles, bool resetPage)
    {
        _items = tiles;
        if (resetPage)
        {
            var current = tiles.FindIndex(a => a.IsCurrent);
            Page = current >= 0 ? current / PageSize : 0;
        }
        else Page = Math.Clamp(Page, 0, PageCount - 1);
        Hover = default;
        Selected = -1;
    }

    /// <summary>条件に合う最初のタイルのページへ移る (グループから戻ったとき、そのグループを見せるため)。</summary>
    public void ShowPageOf(Predicate<AvatarItem> match)
    {
        var index = _items.FindIndex(match);
        if (index >= 0) Page = index / PageSize;
    }

    public void SetChips(List<string> chips)
    {
        _chips = chips;
        ChipStart = Math.Clamp(ChipStart, 0, _chips.Count);
        _chipLayout = null;
    }

    public void TurnPage(int delta)
    {
        Page = Math.Clamp(Page + delta, 0, PageCount - 1);
        Hover = default;
        Selected = -1;
    }

    public void ScrollChips(int delta)
    {
        ChipStart = Math.Clamp(ChipStart + delta, 0, _chips.Count);
        _chipLayout = null;
    }

    // ---------------- 当たり判定 ----------------

    public Hit HitTest(double x, double y)
    {
        var p = new Point(x, y);
        if ((p - CloseCenter).Length <= CloseRadius + 6) return new Hit(HitKind.Close);
        for (var i = 0; i < Tabs.Length; i++)
            if (TabRect(i).Contains(p)) return new Hit(HitKind.Tab, i);
        if (SortRecentRect.Contains(p)) return new Hit(HitKind.SortRecent);
        if (SortNameRect.Contains(p)) return new Hit(HitKind.SortName);
        if (ColumnsLessRect.Contains(p)) return Columns > MinColumns ? new Hit(HitKind.ColumnsLess) : default;
        if (ColumnsMoreRect.Contains(p)) return Columns < MaxColumns ? new Hit(HitKind.ColumnsMore) : default;
        if (GroupToggleRect.Contains(p)) return new Hit(HitKind.GroupToggle);
        if (OpenGroupName is not null)
        {
            if (BackRect.Contains(p)) return new Hit(HitKind.Back);
        }
        else
        {
            foreach (var chip in LayoutChips())
                if (chip.Rect.Contains(p)) return chip.Hit;
        }
        if (PrevRect.Contains(p)) return Page > 0 ? new Hit(HitKind.Prev) : default;
        if (NextRect.Contains(p)) return Page < PageCount - 1 ? new Hit(HitKind.Next) : default;
        for (var slot = 0; slot < PageItemCount; slot++)
            if (TileRect(slot).Contains(p)) return new Hit(HitKind.Tile, PageStart + slot);
        return default;
    }

    private static Rect TabRect(int i)
    {
        var w = TabsRect.Width / Tabs.Length;
        return new Rect(TabsRect.X + i * w, TabsRect.Y, w, TabsRect.Height);
    }

    /// <summary>今のページの slot 番目 (左上から 0) のタイルの場所。</summary>
    public Rect TileRect(int slot)
    {
        var g = _grid;
        var col = slot % g.Columns;
        var row = slot / g.Columns;
        return new Rect(Pad + col * (g.TileWidth + g.Gap), GridTop + row * (g.TileHeight + g.Gap), g.TileWidth, g.TileHeight);
    }

    private readonly record struct ChipSlot(Rect Rect, Hit Hit, string Label, bool Selected);

    /// <summary>
    /// 絞り込みのチップを 1 行に並べる。0 番は「すべて」、以降は Chips の順。
    /// 収まらないぶんは左右の矢印で送る (VR では 2 行に折り返すより、送るほうが押し間違えにくい)。
    /// 候補が無ければ何も出さない。
    /// 指している間は毎フレーム当たり判定で使うので、並べた結果は候補・送り位置・選択が変わるまで使い回す
    /// (文字の幅を測るので、毎回並べ直すと 1 回 40µs ほどかかり、ゴミも出る)。
    /// </summary>
    private List<ChipSlot> LayoutChips() => _chipLayout ??= ArrangeChips();

    private List<ChipSlot>? _chipLayout;

    private List<ChipSlot> ArrangeChips()
    {
        var slots = new List<ChipSlot>();
        if (_chips.Count == 0) return slots;
        var left = Pad;
        var right = Width - Pad;
        if (ChipStart > 0)
        {
            slots.Add(new ChipSlot(new Rect(left, ChipTop, ChipArrowWidth, ChipHeight), new Hit(HitKind.ChipPrev), GlyphLeft, false));
            left += ChipArrowWidth + ChipGap;
        }
        var total = _chips.Count + 1;
        for (var i = ChipStart; i < total; i++)
        {
            var label = i == 0 ? "すべて" : _chips[i - 1];
            var w = Math.Min(Text(label, UiBoldFace, 16, TextBrush, 0).Width + 28, right - Pad - ChipArrowWidth * 2 - ChipGap * 2);
            var reserve = i < total - 1 ? ChipArrowWidth + ChipGap : 0; // まだ後ろがあるなら、送る矢印の場所を残す
            if (left + w > right - reserve && i > ChipStart)
            {
                slots.Add(new ChipSlot(new Rect(right - ChipArrowWidth, ChipTop, ChipArrowWidth, ChipHeight), new Hit(HitKind.ChipNext), GlyphRight, false));
                break;
            }
            var selected = i == 0 ? Filter is null : Filter == _chips[i - 1];
            slots.Add(new ChipSlot(new Rect(left, ChipTop, w, ChipHeight), new Hit(HitKind.Chip, i), label, selected));
            left += w + ChipGap;
        }
        return slots;
    }

    // ---------------- 描画 ----------------

    /// <summary>
    /// 描いて、SteamVR に渡す形 (RGBA) で返す。返す配列は使い回しなので、次に描くまでの間だけ有効。
    /// </summary>
    public byte[] RenderRgba()
    {
        Render();
        CopyRgba(_target, _rgba);
        return _rgba;
    }

    /// <summary>描いた画像 (使い回しの描画先。次に描くまでの間だけ有効)。PNG への書き出しなどに使う。</summary>
    public BitmapSource Render()
    {
        SyncAccent();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(CardBrush, EdgePen, new Rect(1, 1, Width - 2, Height - 2), 24, 24);

            // タブ
            dc.DrawRoundedRectangle(TileBrush, null, TabsRect, 14, 14);
            for (var i = 0; i < Tabs.Length; i++)
            {
                var r = TabRect(i);
                var selected = Tab == Tabs[i];
                if (selected || (Hover.Kind == HitKind.Tab && Hover.Index == i))
                    dc.DrawRoundedRectangle(selected ? AccentBrush : TileHoverBrush, null, new Rect(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8), 10, 10);
                DrawCentered(dc, TabLabels[i], UiBoldFace, 19, selected ? OnAccentBrush : TextBrush, Center(r));
            }
            dc.DrawEllipse(Hover.Kind == HitKind.Close ? TileHoverBrush : TileBrush, null, CloseCenter, CloseRadius, CloseRadius);
            DrawCentered(dc, GlyphClose, IconFace, 20, TextBrush, CloseCenter);

            // 並び順とグループ化
            DrawPill(dc, SortRecentRect, "最近使った順", SortKey.StartsWith("recent", StringComparison.Ordinal), Hover.Kind == HitKind.SortRecent);
            DrawPill(dc, SortNameRect, "名前順", SortKey.StartsWith("name", StringComparison.Ordinal), Hover.Kind == HitKind.SortName);
            // 列数: [−] n 列 [+]
            DrawNav(dc, ColumnsLessRect, GlyphMinus, Columns > MinColumns, Hover.Kind == HitKind.ColumnsLess);
            DrawNav(dc, ColumnsMoreRect, GlyphPlus, Columns < MaxColumns, Hover.Kind == HitKind.ColumnsMore);
            DrawCentered(dc, $"{Columns} 列", UiBoldFace, 16, TextBrush,
                Center(new Rect(ColumnsLessRect.Right, ColumnsLessRect.Y, ColumnsMoreRect.X - ColumnsLessRect.Right, ColumnsLessRect.Height)));
            DrawPill(dc, GroupToggleRect, GroupView ? "グループ化: オン" : "グループ化: オフ", GroupView, Hover.Kind == HitKind.GroupToggle);

            // 絞り込み / 開いているグループ
            if (OpenGroupName is not null)
            {
                DrawNav(dc, BackRect, "", true, Hover.Kind == HitKind.Back);
                DrawCentered(dc, GlyphLeft, IconFace, 18, TextBrush, new Point(BackRect.X + 26, BackRect.Y + BackRect.Height / 2));
                DrawText(dc, "戻る", UiBoldFace, 17, TextBrush, new Point(BackRect.X + 46, BackRect.Y + 9), 60);
                DrawText(dc, $"{OpenGroupName} ({_items.Count} 体)", UiBoldFace, 20, TextBrush,
                    new Point(BackRect.Right + 14, ChipTop + 7), Width - Pad - BackRect.Right - 14);
            }
            else
            {
                foreach (var chip in LayoutChips())
                {
                    var hover = Hover == chip.Hit;
                    var arrow = chip.Hit.Kind is HitKind.ChipPrev or HitKind.ChipNext;
                    dc.DrawRoundedRectangle(chip.Selected ? AccentBrush : hover ? TileHoverBrush : TileBrush, null, chip.Rect, ChipHeight / 2, ChipHeight / 2);
                    DrawCentered(dc, chip.Label, arrow ? IconFace : UiBoldFace, 16, chip.Selected ? OnAccentBrush : TextBrush,
                        Center(chip.Rect), arrow ? 0 : chip.Rect.Width - 16);
                }
            }

            if (_items.Count == 0 && Status.Length == 0)
                DrawText(dc, Filter is not null ? "一致するアバターなし" : "アバターなし",
                    UiFace, 20, MutedBrush, new Point(Pad, GridTop + 20), Width - Pad * 2, maxLines: 3);

            for (var slot = 0; slot < PageItemCount; slot++)
                DrawTile(dc, _grid, TileRect(slot), PageItem(slot), Hover.Kind == HitKind.Tile && Hover.Index == PageStart + slot,
                    Selected == PageStart + slot);

            if (Status.Length > 0)
                DrawCentered(dc, Status, UiFace, 16, StatusIsError ? DangerBrush : MutedBrush, new Point(Width / 2.0, StatusY), Width - Pad * 2);
            else if (Selected >= 0 && Selected < _items.Count)
                DrawCentered(dc, $"もう一度押すとチェンジ: {_items[Selected].Name}", UiBoldFace, 16, TextBrush, new Point(Width / 2.0, StatusY), Width - Pad * 2);
            else if (!_grid.ShowName && Hover.Kind == HitKind.Tile && Hover.Index >= 0 && Hover.Index < _items.Count)
                DrawCentered(dc, _items[Hover.Index].Name, UiBoldFace, 16, TextBrush, new Point(Width / 2.0, StatusY), Width - Pad * 2); // 名前を載せないほど小さいとき

            if (PageCount > 1)
            {
                DrawNav(dc, PrevRect, GlyphLeft, Page > 0, Hover.Kind == HitKind.Prev);
                DrawNav(dc, NextRect, GlyphRight, Page < PageCount - 1, Hover.Kind == HitKind.Next);
                DrawCentered(dc, $"{Page + 1} / {PageCount}", UiFace, 20, MutedBrush, Center(new Rect(PrevRect.Right, PrevRect.Y, NextRect.X - PrevRect.Right, PrevRect.Height)));
            }
        }
        _target.Clear();
        _target.Render(visual);
        return _target;
    }

    private static void DrawTile(DrawingContext dc, GridLayout g, Rect r, AvatarItem item, bool hover, bool selected)
    {
        var s = g.Scale;
        var radius = Math.Clamp(14 * s, 6, 14);
        var inset = Math.Clamp(8 * s, 3, 8);
        dc.PushClip(new RectangleGeometry(r, radius, radius));
        dc.DrawRectangle(hover || selected ? TileHoverBrush : TileBrush, null, r);
        var thumbRect = new Rect(r.X, r.Y, r.Width, g.ThumbHeight);
        dc.DrawRectangle(ThumbBackBrush, null, thumbRect);
        if (item.Thumbnail is { } image) // グループは代表のサムネイル
        {
            // 枠いっぱいに切り抜いて表示 (UniformToFill と同じ)
            var scale = Math.Max(thumbRect.Width / image.Width, thumbRect.Height / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            dc.PushClip(new RectangleGeometry(thumbRect));
            dc.DrawImage(image, new Rect(thumbRect.X + (thumbRect.Width - w) / 2, thumbRect.Y + (thumbRect.Height - h) / 2, w, h));
            dc.Pop();
        }
        else DrawCentered(dc, GlyphPerson, IconFace, Math.Max(16, 40 * s), MutedBrush, Center(thumbRect));

        if (item.IsUnavailable)
        {
            // 使えなくなったもの (パブリック): 暗くして印を付ける。押せば着替えは試せる
            dc.DrawRectangle(DimBrush, null, thumbRect);
            DrawBadge(dc, "使用不可", DangerBrush, new Point(r.X + inset, r.Y + inset), s, r.Width - inset * 2);
        }
        if (item.IsGroup)
        {
            DrawBadge(dc, g.TileWidth >= 120 ? $"グループ · {item.Count} 体" : $"{item.Count} 体", TextBrush,
                new Point(r.X + inset, thumbRect.Bottom - inset - BadgeHeight(s)), s, r.Width - inset * 2);
        }
        if (g.ShowName)
            DrawText(dc, item.Name, UiBoldFace, g.NameSize, TextBrush, new Point(r.X + 10 * s, r.Y + g.ThumbHeight + 7 * s), r.Width - 20 * s);
        dc.Pop();

        if (selected)
        {
            // 選んだタイル: 白い枠 (「今着ている」のアクセント色の枠とは別の色)。今着ているものなら、その枠の内側に描く
            var thin = g.TileWidth < 120;
            var edge = (thin ? 1.25 : 2) + (item.IsCurrent ? (thin ? 2.5 : 4) : 0);
            var corner = Math.Max(1, radius - edge);
            dc.DrawRoundedRectangle(null, thin ? SelectedThinPen : SelectedPen,
                new Rect(r.X + edge, r.Y + edge, r.Width - edge * 2, r.Height - edge * 2), corner, corner);
        }
        if (item.IsCurrent)
        {
            var thin = g.TileWidth < 120;
            var edge = thin ? 1.25 : 2;
            dc.DrawRoundedRectangle(null, thin ? AccentThinPen : AccentPen,
                new Rect(r.X + edge, r.Y + edge, r.Width - edge * 2, r.Height - edge * 2), radius - 2, radius - 2);
            var markRadius = Math.Clamp(15 * s, 7, 15);
            var mark = new Point(r.Right - inset - markRadius + 1, r.Y + inset + markRadius - 1);
            dc.DrawEllipse(AccentBrush, null, mark, markRadius, markRadius);
            DrawCentered(dc, GlyphCheck, IconFace, markRadius * 1.07, OnAccentBrush, mark);
        }
    }

    private static double BadgeHeight(double scale) => Math.Round(Math.Clamp(14 * scale, 9, 14) * 1.86);

    /// <summary>サムネイルの上に載せる小さな札 (使用不可 / グループ)。タイルが小さいほど小さく描く。</summary>
    private static void DrawBadge(DrawingContext dc, string text, Brush foreground, Point at, double scale, double maxWidth)
    {
        var size = Math.Clamp(14 * scale, 9, 14);
        var pad = Math.Clamp(8 * scale, 3, 8);
        var ft = Text(text, UiBoldFace, size, foreground, Math.Max(1, maxWidth - pad * 2));
        var height = BadgeHeight(scale);
        dc.DrawRoundedRectangle(BadgeBrush, null, new Rect(at.X, at.Y, Math.Min(ft.Width + pad * 2, maxWidth), height), pad, pad);
        dc.DrawText(ft, new Point(at.X + pad, at.Y + (height - ft.Height) / 2));
    }

    private static void DrawPill(DrawingContext dc, Rect r, string label, bool selected, bool hover)
    {
        dc.DrawRoundedRectangle(selected ? AccentBrush : hover ? TileHoverBrush : TileBrush, null, r, r.Height / 2, r.Height / 2);
        DrawCentered(dc, label, UiBoldFace, 18, selected ? OnAccentBrush : TextBrush, Center(r));
    }

    private static void DrawNav(DrawingContext dc, Rect r, string glyph, bool enabled, bool hover)
    {
        dc.DrawRoundedRectangle(hover && enabled ? TileHoverBrush : TileBrush, null, r, 12, 12);
        if (glyph.Length > 0) DrawCentered(dc, glyph, IconFace, 22, enabled ? TextBrush : MutedBrush, Center(r));
    }

    /// <summary>アクセント色が変わっていたら、それを使うブラシとペン、作り置きの手首ボタンを作り直す。描く前に呼ぶ。</summary>
    private static void SyncAccent()
    {
        var c = App.AccentColor(dark: true);
        if (c == _accentColor && AccentBrush != Brushes.Transparent) return;
        _accentColor = c;
        AccentBrush = Frozen(c);
        OnAccentBrush = Frozen(AccentPalette.OnAccentFor(c, dark: true));
        AccentPen = FrozenPen(AccentBrush, 4);
        AccentThinPen = FrozenPen(AccentBrush, 2.5);
        ButtonRingPen = FrozenPen(AccentBrush, 6);
        _buttonRgba = null;
        _buttonOpenRgba = null;
    }

    // 手首ボタンは「チェンジ」「閉じる」の 2 通りしかないので、1 回だけ作って持っておく (色が変わったら作り直す)
    private static byte[]? _buttonRgba;
    private static byte[]? _buttonOpenRgba;

    /// <summary>手首ボタンの画像 (RGBA)。作るのは最初の 1 回だけ。</summary>
    public static byte[] ButtonRgba(bool panelOpen)
    {
        SyncAccent();
        ref var cached = ref panelOpen ? ref _buttonOpenRgba : ref _buttonRgba;
        if (cached is null)
        {
            cached = new byte[ButtonSize * ButtonSize * 4];
            CopyRgba(RenderButton(panelOpen), cached);
        }
        return cached;
    }

    /// <summary>手首に付けるボタン。暗い半透明の丸に、色付きの細い輪と人のアイコン、「チェンジ」。</summary>
    public static BitmapSource RenderButton(bool panelOpen)
    {
        SyncAccent();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var c = new Point(ButtonSize / 2.0, ButtonSize / 2.0);
            var r = ButtonSize / 2.0 - 6; // 輪の太さぶん内側に (外に切れないように)
            dc.DrawEllipse(ButtonBackBrush, panelOpen ? ButtonRingOpenPen : ButtonRingPen, c, r, r);
            DrawCentered(dc, panelOpen ? GlyphClose : GlyphPerson, IconFace, 70, panelOpen ? TextBrush : AccentBrush, new Point(c.X, c.Y - 16));
            DrawCentered(dc, panelOpen ? "閉じる" : "チェンジ", UiBoldFace, 28, TextBrush, new Point(c.X, c.Y + 46));
        }
        return ToBitmap(visual, ButtonSize, ButtonSize);
    }

    // ---------------- カーソル ----------------

    private static byte[]? _cursorRgba;

    /// <summary>レイが当たった所に出すカーソルの画像 (RGBA)。作るのは最初の 1 回だけ。</summary>
    public static byte[] CursorRgba()
    {
        if (_cursorRgba is null)
        {
            _cursorRgba = new byte[CursorSize * CursorSize * 4];
            CopyRgba(RenderCursor(), _cursorRgba);
        }
        return _cursorRgba;
    }

    /// <summary>白い丸に黒い縁 (明るい所でも暗い所でも見える)。</summary>
    public static BitmapSource RenderCursor()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var c = new Point(CursorSize / 2.0, CursorSize / 2.0);
            dc.DrawEllipse(Brushes.White, FrozenPen(Frozen(Color.FromArgb(0xCC, 0, 0, 0)), 8), c, CursorSize / 2.0 - 6, CursorSize / 2.0 - 6);
        }
        return ToBitmap(visual, CursorSize, CursorSize);
    }

    // ---------------- 小物 ----------------

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static FormattedText Text(string s, Typeface face, double size, Brush brush, double maxWidth, int maxLines = 1)
    {
        var ft = new FormattedText(s, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, face, size, brush, 1.0)
        {
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = maxLines,
        };
        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    private static void DrawText(DrawingContext dc, string s, Typeface face, double size, Brush brush, Point at, double maxWidth, int maxLines = 1)
        => dc.DrawText(Text(s, face, size, brush, maxWidth, maxLines), at);

    private static void DrawCentered(DrawingContext dc, string s, Typeface face, double size, Brush brush, Point center, double maxWidth = 0)
    {
        var ft = Text(s, face, size, brush, maxWidth);
        if (maxWidth > 0) ft.TextAlignment = TextAlignment.Center;
        var x = maxWidth > 0 ? center.X - maxWidth / 2 : center.X - ft.Width / 2;
        dc.DrawText(ft, new Point(x, center.Y - ft.Height / 2));
    }

    private static BitmapSource ToBitmap(Visual visual, int w, int h)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// SteamVR に渡す形 (RGBA、上の行から) にして dest へ書く。WPF は BGRA なので R と B を入れ替える。
    /// 1 画素を 4 バイトまとめて扱い、ビット演算で入れ替える (1 バイトずつより数倍速い)。
    /// </summary>
    public static void CopyRgba(BitmapSource bmp, byte[] dest)
    {
        bmp.CopyPixels(dest, bmp.PixelWidth * 4, 0);
        // メモリ上は B G R A の順 = リトルエンディアンの uint で 0xAARRGGBB。R と B を入れ替えて 0xAABBGGRR にする
        foreach (ref var px in MemoryMarshal.Cast<byte, uint>(dest.AsSpan()))
            px = (px & 0xFF00FF00) | ((px & 0x000000FF) << 16) | ((px >> 16) & 0x000000FF);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var p = new Pen(brush, thickness);
        p.Freeze();
        return p;
    }
}
