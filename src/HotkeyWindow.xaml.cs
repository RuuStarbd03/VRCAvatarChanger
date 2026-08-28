using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>
/// ホットキーの割り当て画面。「変更」を押してから実際にキーを押してもらう方式で、
/// 押された組み合わせをそのまま覚える(キー名を入力させない)。
/// 変更は即座に設定へ書き、閉じたときに呼び出し元がフックを張り直す。
/// </summary>
public partial class HotkeyWindow : Window
{
    private sealed class HotkeyRow
    {
        public required string Title { get; init; }
        public required Func<Hotkey> Get { get; init; }
        public required Action<Hotkey> Set { get; init; }
        /// <summary>アバター割り当てのみ: 行ごと消す。</summary>
        public Action? Remove { get; init; }
        public TextBlock KeyText { get; set; } = null!;
    }

    private readonly Settings _settings;
    private readonly Action _save;
    private readonly List<HotkeyRow> _rows = [];
    private readonly Dictionary<AvatarHotkey, HotkeyRow> _avatarRows = [];
    private HotkeyRow? _capturing;

    /// <param name="assign">一覧の右クリックから来た場合、割り当て先のアバター。開いてすぐキー入力待ちにする。</param>
    public HotkeyWindow(Settings settings, Action save, (string Id, string Name)? assign = null)
    {
        _settings = settings;
        _save = save;
        InitializeComponent();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        DisabledWarning.Visibility = settings.QuickOverlay ? Visibility.Collapsed : Visibility.Visible;

        AvatarHotkey? target = null;
        if (assign is { } a && VRChatApi.IsValidAvatarId(a.Id))
        {
            target = settings.AvatarHotkeys.FirstOrDefault(h => h.AvatarId == a.Id);
            if (target is null)
            {
                target = new AvatarHotkey { AvatarId = a.Id, Name = a.Name, Key = "" };
                settings.AvatarHotkeys.Add(target);
            }
            else target.Name = a.Name; // 名前が変わっていることがある
        }

        BuildRows();
        // 右クリックから来たときは、そのアバターの行をすぐキー入力待ちにする
        if (target is not null && _avatarRows.TryGetValue(target, out var targetRow))
            Loaded += (_, _) => StartCapture(targetRow);
        Closed += (_, _) =>
        {
            // キーを決めずに閉じた割り当ては残さない
            _settings.AvatarHotkeys.RemoveAll(h => !Hotkey.Parse(h.Key).IsSet);
            _save();
        };
    }

    private static string DisplayName(AvatarHotkey h) => string.IsNullOrWhiteSpace(h.Name) ? h.AvatarId : h.Name;

    private void BuildRows()
    {
        _rows.Clear();
        _avatarRows.Clear();
        ActionRows.Children.Clear();
        AvatarRows.Children.Clear();

        AddRow(ActionRows, new HotkeyRow
        {
            Title = "クイック着替えを開く",
            Get = () => Hotkey.Parse(_settings.QuickHotkey),
            Set = h => _settings.QuickHotkey = h.ToString(),
        }, "画面の右にアバター選択を重ねて出します。");

        AddRow(ActionRows, new HotkeyRow
        {
            Title = "直前のアバターに戻す",
            Get = () => Hotkey.Parse(_settings.PreviousHotkey),
            Set = h => _settings.PreviousHotkey = h.ToString(),
        }, "1 つ前に着ていたアバターと行き来できます。");

        AddRow(ActionRows, new HotkeyRow
        {
            Title = "グループ内の次のアバター",
            Get = () => Hotkey.Parse(_settings.NextInGroupHotkey),
            Set = h => _settings.NextInGroupHotkey = h.ToString(),
        }, "今のアバターが入っているグループの中で、衣装違いを順に送ります。");

        foreach (var entry in _settings.AvatarHotkeys.ToList())
        {
            var row = new HotkeyRow
            {
                Title = DisplayName(entry),
                Get = () => Hotkey.Parse(entry.Key),
                Set = h => entry.Key = h.ToString(),
                Remove = () => { _settings.AvatarHotkeys.Remove(entry); _save(); BuildRows(); },
            };
            AddRow(AvatarRows, row, entry.AvatarId);
            _avatarRows[entry] = row;
        }
        AvatarHint.Text = _settings.AvatarHotkeys.Count > 0
            ? "一覧でアバターを右クリック →「ホットキーに割り当てる...」で追加できます。"
            : "まだありません。一覧でアバターを右クリック →「ホットキーに割り当てる...」で追加できます。";
    }

    private void AddRow(Panel host, HotkeyRow row, string description)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = row.Title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(ForegroundProperty, "TextBrush"); // 配色を切り替えても追従するように
        titles.Children.Add(title);
        titles.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("CaptionText"),
            // 説明は折り返し、アバター行の説明 (ID) だけは 1 行に収める
            TextWrapping = row.Remove is null ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        grid.Children.Add(titles);

        row.KeyText = new TextBlock
        {
            Text = row.Get().Display,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MinWidth = 120,
            Margin = new Thickness(12, 0, 12, 0),
        };
        row.KeyText.SetResourceReference(ForegroundProperty, row.Get().IsSet ? "TextBrush" : "MutedTextBrush");
        Grid.SetColumn(row.KeyText, 1);
        grid.Children.Add(row.KeyText);

        var change = new Button
        {
            Content = "変更",
            Style = (Style)FindResource("SecondaryButton"),
            MinWidth = 70,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
        };
        change.Click += (_, _) => StartCapture(row);
        Grid.SetColumn(change, 2);
        grid.Children.Add(change);

        var clear = new Button
        {
            Content = row.Remove is null ? "解除" : "削除",
            Style = (Style)FindResource("GhostButton"),
            MinWidth = 60,
            Height = 30,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        clear.Click += (_, _) =>
        {
            CancelCapture();
            if (row.Remove is not null) row.Remove();
            else Apply(row, Hotkey.None);
        };
        Grid.SetColumn(clear, 3);
        grid.Children.Add(clear);

        host.Children.Add(new Border { Style = (Style)FindResource("RowCard"), Child = grid });
        _rows.Add(row);
    }

    private void StartCapture(HotkeyRow row)
    {
        CancelCapture();
        _capturing = row;
        row.KeyText.Text = "キーを押してください...";
        row.KeyText.SetResourceReference(ForegroundProperty, "AccentBrush");
        CaptureHint.Text = "使いたいキーの組み合わせを押してください(Esc で中止、Backspace で解除)。";
        Focus();
    }

    private void CancelCapture()
    {
        if (_capturing is null) return;
        RefreshRow(_capturing);
        _capturing = null;
    }

    private void RefreshRow(HotkeyRow row)
    {
        var current = row.Get();
        row.KeyText.Text = current.Display;
        row.KeyText.SetResourceReference(ForegroundProperty, current.IsSet ? "TextBrush" : "MutedTextBrush");
    }

    private void Apply(HotkeyRow row, Hotkey hotkey)
    {
        row.Set(hotkey);
        _save();
        RefreshRow(row);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            return;
        }
        e.Handled = true;
        var row = _capturing;
        // Alt を含む組み合わせは Key.System で来る
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { CancelCapture(); return; }
        if (key is Key.Back or Key.Delete)
        {
            _capturing = null;
            Apply(row, Hotkey.None);
            CaptureHint.Text = $"「{row.Title}」の割り当てを解除しました。";
            return;
        }
        if (Hotkey.IsModifierKey(key)) return; // 修飾キーだけでは決まらない。本命のキーを待つ

        var hotkey = new Hotkey(Keyboard.Modifiers, key);
        // 修飾キー無しだと、ゲーム中の普通の操作(移動など)を奪ってしまう。ファンクションキーだけ例外
        if (hotkey.Modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24))
        {
            CaptureHint.Text = "Ctrl / Shift / Alt と組み合わせるか、ファンクションキーを使ってください。";
            return;
        }
        var conflict = _rows.FirstOrDefault(r => !ReferenceEquals(r, row) && r.Get() == hotkey);
        if (conflict is not null)
        {
            CaptureHint.Text = $"{hotkey.Display} は「{conflict.Title}」に割り当て済みです。別のキーを押してください。";
            return;
        }
        _capturing = null;
        Apply(row, hotkey);
        CaptureHint.Text = $"「{row.Title}」を {hotkey.Display} にしました。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
