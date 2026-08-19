using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>パブリックアバターのタグを編集する小さなダイアログ。</summary>
public partial class TagPickerWindow : Window
{
    /// <summary>OK で閉じたときのタグ一覧。</summary>
    public List<string>? Result { get; private set; }

    public TagPickerWindow(string subject, IEnumerable<string> allTags, IEnumerable<string> current)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        SubjectText.Text = subject;
        var currentSet = new HashSet<string>(current, StringComparer.CurrentCultureIgnoreCase);
        foreach (var t in allTags) AddCheckBox(t, currentSet.Contains(t));
        UpdateListVisibility();
        Loaded += (_, _) => NewTagBox.Focus();
    }

    private void AddCheckBox(string tag, bool isChecked)
    {
        TagList.Children.Add(new CheckBox
        {
            Content = tag,
            IsChecked = isChecked,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 3, 0, 3),
        });
    }

    private void UpdateListVisibility()
        => TagListPanel.Visibility = TagList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        var tag = NewTagBox.Text.Trim();
        if (tag.Length == 0) return;
        var existing = TagList.Children.OfType<CheckBox>()
            .FirstOrDefault(c => string.Equals((string)c.Content, tag, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) existing.IsChecked = true;
        else AddCheckBox(tag, true);
        NewTagBox.Clear();
        NewTagBox.Focus();
        UpdateListVisibility();
    }

    private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; AddTag_Click(sender, e); }
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 入力欄に残っている文字も「付けるつもりだった」とみなす
        if (NewTagBox.Text.Trim().Length > 0) AddTag_Click(sender, e);
        Result = TagList.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Content)
            .ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
