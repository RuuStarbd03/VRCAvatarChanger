using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VRCAvatarChanger;

/// <summary>
/// 一覧の 1 要素。アバター 1 体、または複数のアバターをまとめたグループ(代表アバターのサムネで表示)。
/// </summary>
public sealed class AvatarItem : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private bool _isDropTarget;

    /// <summary>アバター(グループの場合は代表 = 先頭メンバー)。</summary>
    public Avatar Avatar { get; }
    /// <summary>パブリックリストに追加した日時(パブリックタブのみ)。並び順の「追加日」に使う。</summary>
    public DateTimeOffset? AddedAt { get; init; }
    private IReadOnlyList<string> _tags = [];
    /// <summary>ユーザーが付けたタグ(自分のアバター / パブリックタブ)。</summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set { _tags = value; OnPropertyChanged(); OnPropertyChanged(nameof(Badge)); }
    }

    public AvatarGroup? Group { get; }
    public IReadOnlyList<AvatarItem> Members { get; } = [];
    /// <summary>グループの代表メンバー(一番古いもの)。</summary>
    public AvatarItem? Representative { get; }
    public bool IsGroup => Group is not null;
    public bool IsAvatar => Group is null;
    public int Count => IsGroup ? Members.Count : 1;

    public AvatarItem(Avatar avatar) { Avatar = avatar; }

    // 弱イベントの購読を保持する(タイル自身が生きている間だけ通知を受け、破棄されたら購読ごと回収される)
    private readonly EventHandler<PropertyChangedEventArgs>? _repThumbnailHandler;

    public AvatarItem(AvatarGroup group, IReadOnlyList<AvatarItem> members)
    {
        Group = group;
        Members = members;
        // 代表は「一番古い」メンバー(追加日 / 作成日が最も古いもの)。並び順もサムネもこれに従う
        var rep = members
            .OrderBy(m => m.AddedAt ?? m.Avatar.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(m => m.Avatar.UpdatedAt ?? DateTimeOffset.MaxValue)
            .First();
        Representative = rep;
        Avatar = rep.Avatar;
        AddedAt = rep.AddedAt;
        // 代表のサムネが後から届いたら自分の表示も更新する。
        // グループタイルは絞り込みのたびに作り直されるため、通常の += だと古いタイルへの
        // 購読が代表メンバー側に溜まり続ける。弱イベントで購読して自然に回収させる
        _repThumbnailHandler = (_, e) => { if (e.PropertyName == nameof(Thumbnail)) OnPropertyChanged(nameof(Thumbnail)); };
        PropertyChangedEventManager.AddHandler(rep, _repThumbnailHandler, nameof(Thumbnail));
    }

    public string Id => Avatar.Id;
    public string Name => Group?.Name ?? Avatar.Name;
    public string AuthorName => IsGroup ? $"{Members.Count} 体" : Avatar.AuthorName;

    private string? _subText;
    /// <summary>
    /// 名前の下に出す 1 行。並び順に合わせて中身を変える (日付順なら日付、パフォーマンス順ならランク)。
    /// 何も指定されていなければ作者名。
    /// </summary>
    public string SubText
    {
        get => _subText ?? AuthorName;
        set { _subText = value; OnPropertyChanged(); }
    }

    private string? _subText2;
    /// <summary>
    /// さらにもう 1 行。1 行に収めると狭い列で切れてしまうものを分ける
    /// (パフォーマンス順のランクなど)。空のときはその行ごと消える。
    /// </summary>
    public string? SubText2
    {
        get => _subText2;
        set
        {
            _subText2 = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSubText2));
        }
    }

    public bool HasSubText2 => _subText2 is not null;
    public string Badge => IsGroup ? "グループ"
        : Tags.Count > 0 ? string.Join(", ", Tags)
        : !string.IsNullOrEmpty(Avatar.FavoriteGroup) ? Avatar.FavoriteGroup
        : Avatar.ReleaseStatus == "private" ? "非公開" : Avatar.ReleaseStatus == "public" ? "公開" : "";
    public string? ThumbnailUrl => Avatar.ThumbnailImageUrl ?? Avatar.ImageUrl;

    public BitmapImage? Thumbnail
    {
        get => IsGroup ? Representative!.Thumbnail : _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    /// <summary>今持っているサムネイルをデコードした幅。表示形式が変わって足りなくなったら読み直す目印。</summary>
    public int ThumbnailWidth { get; set; }

    private Brush? _stripeBrush;
    /// <summary>10 刻み色分け(隠し機能)の背景色。無効時や除外時は null。</summary>
    public Brush? StripeBrush
    {
        get => _stripeBrush;
        set { if (!ReferenceEquals(_stripeBrush, value)) { _stripeBrush = value; OnPropertyChanged(); } }
    }

    private bool _isUnavailable;
    /// <summary>
    /// 使えなくなったアバター (削除された / 非公開になった)。パブリックリストで、取り直しに続けて失敗して確定したもの。
    /// グループなら「中の全員が使えない」とき。
    /// </summary>
    public bool IsUnavailable
    {
        get => IsGroup ? Members.Count > 0 && Members.All(m => m.IsUnavailable) : _isUnavailable;
        set
        {
            if (_isUnavailable == value) return;
            _isUnavailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UnavailableText));
        }
    }

    /// <summary>「使えない」と確定した日時 (表示用)。</summary>
    public DateTimeOffset? UnavailableSince { get; set; }
    /// <summary>使えない理由。"deleted" / "private"。</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>ツールチップや詳細に出す 1 行。「2026/09/01 に確認: 削除されたか非公開になりました」。</summary>
    public string UnavailableText
    {
        get
        {
            if (!IsUnavailable) return "";
            var what = UnavailableReason == "private" ? "非公開になりました" : "削除されたか非公開になりました";
            return UnavailableSince is { } at ? $"{at.ToLocalTime():yyyy/MM/dd HH:mm} に確認: {what}" : what;
        }
    }

    private bool _isCurrent;
    /// <summary>現在着ているアバター(グループの場合は中に現在のアバターがいる)。チェックバッジの表示に使う。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent != value) { _isCurrent = value; OnPropertyChanged(); } }
    }

    /// <summary>ドラッグ中に重ねられているとき true(見た目のハイライト用)。</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget != value) { _isDropTarget = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>幅から 4:3 の高さを作る。ConverterParameter="clip" の場合は角丸クリップ用の RectangleGeometry を返す。</summary>
public sealed class AspectHeightConverter : IValueConverter, IMultiValueConverter
{
    public double Ratio { get; set; } = 0.75;

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is double w && !double.IsNaN(w) ? Math.Max(0, w * Ratio) : 0d;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();

    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var w = values.Length > 0 && values[0] is double a && !double.IsNaN(a) ? a : 0;
        var h = values.Length > 1 && values[1] is double b && !double.IsNaN(b) ? b : 0;
        var geo = new RectangleGeometry(new Rect(0, 0, w, h), 6, 6);
        geo.Freeze();
        return geo;
    }
}
