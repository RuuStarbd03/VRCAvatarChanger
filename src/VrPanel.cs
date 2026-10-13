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
/// 文字入力や細かいスクロールはしにくいので、検索は置かず、大きいタイルをページ送りで見せる。
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

    private const int Columns = 3;
    private const int Rows = 3;
    public const int PageSize = Columns * Rows;

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
    private static readonly Rect SortRecentRect = new(Pad, 80, 150, 44);
    private static readonly Rect SortNameRect = new(Pad + 158, 80, 110, 44);
    private static readonly Rect GroupToggleRect = new(Width - Pad - 190, 80, 190, 44);
    // 3 段目: 絞り込みのチップ (グループを開いているときは「戻る」)
    private const double ChipTop = 134;
    private const double ChipHeight = 40;
    private const double ChipGap = 8;
    private const double ChipArrowWidth = 40;
    private static readonly Rect BackRect = new(Pad, ChipTop, 110, ChipHeight);
    // タイル
    private const double GridTop = 186;
    private const double TileGap = 10;
    private const double ThumbHeight = 128;
    private const double TileHeight = 166;
    private static readonly double TileWidth = (Width - Pad * 2 - TileGap * (Columns - 1)) / Columns;
    // 下: 状態の一言 + ページ送り
    private const double StatusY = 722;
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
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(0xE5, 0x6B, 0x6F));
    private static Pen AccentPen = new();
    private static readonly Pen EdgePen = FrozenPen(Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 2);
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

    /// <summary>押された場所の意味。Index はタブ / チップ / タイルの番号 (タイルは一覧全体での番号)。</summary>
    public enum HitKind { None, Close, Tab, SortRecent, SortName, GroupToggle, Chip, ChipPrev, ChipNext, Back, Tile, Prev, Next }

    public readonly record struct Hit(HitKind Kind, int Index = -1);

    // ---- 表示する状態 (切り替えの判断は VrOverlay が行い、ここは描くだけ) ----
    public string Tab { get; set; } = AvatarTab.Own;
    public string SortKey { get; set; } = "recent_asc";
    public bool GroupView { get; set; } = true;
    /// <summary>選んでいる絞り込み (null = すべて)。</summary>
    public string? Filter { get; set; }
    /// <summary>開いているグループの名前 (null = 開いていない)。</summary>
    public string? OpenGroupName { get; set; }
    public string Status { get; set; } = "";
    public bool StatusIsError { get; set; }
    public Hit Hover { get; set; }

    private List<AvatarItem> _items = [];
    private List<string> _chips = [];
    public IReadOnlyList<AvatarItem> Items => _items;
    public IReadOnlyList<string> Chips => _chips;
    public int Page { get; private set; }
    public int PageCount => Math.Max(1, (_items.Count + PageSize - 1) / PageSize);
    /// <summary>チップが 1 行に収まらないとき、何番目のチップから見せているか (0 = 「すべて」)。</summary>
    public int ChipStart { get; private set; }

    /// <summary>今のページの先頭が一覧の何番目か。</summary>
    public int PageStart => Page * PageSize;

    /// <summary>今のページに並んでいる数 (最後のページは 9 に満たないことがある)。</summary>
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
    }

    public void TurnPage(int delta)
    {
        Page = Math.Clamp(Page + delta, 0, PageCount - 1);
        Hover = default;
    }

    public void ScrollChips(int delta) => ChipStart = Math.Clamp(ChipStart + delta, 0, _chips.Count);

    // ---------------- 当たり判定 ----------------

    public Hit HitTest(double x, double y)
    {
        var p = new Point(x, y);
        if ((p - CloseCenter).Length <= CloseRadius + 6) return new Hit(HitKind.Close);
        for (var i = 0; i < Tabs.Length; i++)
            if (TabRect(i).Contains(p)) return new Hit(HitKind.Tab, i);
        if (SortRecentRect.Contains(p)) return new Hit(HitKind.SortRecent);
        if (SortNameRect.Contains(p)) return new Hit(HitKind.SortName);
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

    private static Rect TileRect(int slot)
    {
        var col = slot % Columns;
        var row = slot / Columns;
        return new Rect(Pad + col * (TileWidth + TileGap), GridTop + row * (TileHeight + TileGap), TileWidth, TileHeight);
    }

    private readonly record struct ChipSlot(Rect Rect, Hit Hit, string Label, bool Selected);

    /// <summary>
    /// 絞り込みのチップを 1 行に並べる。0 番は「すべて」、以降は Chips の順。
    /// 収まらないぶんは左右の矢印で送る (VR では 2 行に折り返すより、送るほうが押し間違えにくい)。
    /// 候補が無ければ何も出さない。
    /// </summary>
    private List<ChipSlot> LayoutChips()
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
                DrawCentered(dc, TabLabels[i], UiBoldFace, 19, selected ? Brushes.White : TextBrush, Center(r));
            }
            dc.DrawEllipse(Hover.Kind == HitKind.Close ? TileHoverBrush : TileBrush, null, CloseCenter, CloseRadius, CloseRadius);
            DrawCentered(dc, GlyphClose, IconFace, 20, TextBrush, CloseCenter);

            // 並び順とグループ化
            DrawPill(dc, SortRecentRect, "最近使った順", SortKey.StartsWith("recent", StringComparison.Ordinal), Hover.Kind == HitKind.SortRecent);
            DrawPill(dc, SortNameRect, "名前順", SortKey.StartsWith("name", StringComparison.Ordinal), Hover.Kind == HitKind.SortName);
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
                    DrawCentered(dc, chip.Label, arrow ? IconFace : UiBoldFace, 16, chip.Selected ? Brushes.White : TextBrush,
                        Center(chip.Rect), arrow ? 0 : chip.Rect.Width - 16);
                }
            }

            if (_items.Count == 0 && Status.Length == 0)
                DrawText(dc, Filter is not null ? "一致するアバターなし" : "アバターなし",
                    UiFace, 20, MutedBrush, new Point(Pad, GridTop + 20), Width - Pad * 2, maxLines: 3);

            for (var slot = 0; slot < PageItemCount; slot++)
                DrawTile(dc, TileRect(slot), PageItem(slot), Hover.Kind == HitKind.Tile && Hover.Index == PageStart + slot);

            if (Status.Length > 0)
                DrawCentered(dc, Status, UiFace, 16, StatusIsError ? DangerBrush : MutedBrush, new Point(Width / 2.0, StatusY), Width - Pad * 2);

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

    private static void DrawTile(DrawingContext dc, Rect r, AvatarItem item, bool hover)
    {
        dc.PushClip(new RectangleGeometry(r, 14, 14));
        dc.DrawRectangle(hover ? TileHoverBrush : TileBrush, null, r);
        var thumbRect = new Rect(r.X, r.Y, r.Width, ThumbHeight);
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
        else DrawCentered(dc, GlyphPerson, IconFace, 40, MutedBrush, Center(thumbRect));

        if (item.IsUnavailable)
        {
            // 使えなくなったもの (パブリック): 暗くして印を付ける。押せば着替えは試せる
            dc.DrawRectangle(DimBrush, null, thumbRect);
            DrawBadge(dc, "使用不可", DangerBrush, new Point(r.X + 8, r.Y + 8));
        }
        if (item.IsGroup)
            DrawBadge(dc, $"グループ · {item.Count} 体", TextBrush, new Point(r.X + 8, thumbRect.Bottom - 34));
        DrawText(dc, item.Name, UiBoldFace, 17, TextBrush, new Point(r.X + 10, r.Y + ThumbHeight + 7), r.Width - 20);
        dc.Pop();

        if (item.IsCurrent)
        {
            dc.DrawRoundedRectangle(null, AccentPen, new Rect(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), 12, 12);
            var badge = new Point(r.Right - 22, r.Y + 22);
            dc.DrawEllipse(AccentBrush, null, badge, 15, 15);
            DrawCentered(dc, GlyphCheck, IconFace, 16, Brushes.White, badge);
        }
    }

    private static void DrawBadge(DrawingContext dc, string text, Brush foreground, Point at)
    {
        var ft = Text(text, UiBoldFace, 14, foreground, 0);
        dc.DrawRoundedRectangle(BadgeBrush, null, new Rect(at.X, at.Y, ft.Width + 16, 26), 8, 8);
        dc.DrawText(ft, new Point(at.X + 8, at.Y + (26 - ft.Height) / 2));
    }

    private static void DrawPill(DrawingContext dc, Rect r, string label, bool selected, bool hover)
    {
        dc.DrawRoundedRectangle(selected ? AccentBrush : hover ? TileHoverBrush : TileBrush, null, r, r.Height / 2, r.Height / 2);
        DrawCentered(dc, label, UiBoldFace, 18, selected ? Brushes.White : TextBrush, Center(r));
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
        AccentPen = FrozenPen(AccentBrush, 4);
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
