using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VRCAvatarChanger;

/// <summary>グループの選択 / 新規作成、または名前変更を行う小さなダイアログ。</summary>
public partial class GroupPickerWindow : Window
{
    private readonly GroupStore _store;
    private readonly bool _renameMode;

    /// <summary>選択または作成されたグループ(割り当てモード)。</summary>
    public AvatarGroup? Result { get; private set; }
    /// <summary>新しい名前(名前変更モード)。</summary>
    public string? NewName { get; private set; }

    /// <summary>割り当てモード。</summary>
    public GroupPickerWindow(GroupStore store, string subject, AvatarGroup? current)
    {
        _store = store;
        InitializeComponent();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        SubjectText.Text = subject;
        foreach (var g in store.Groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            GroupList.Items.Add(new ListBoxItem { Content = g.Name, Tag = g });
        if (store.Groups.Count == 0) ExistingPanel.Visibility = Visibility.Collapsed;
        if (current is not null)
            GroupList.SelectedItem = GroupList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == current);
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>名前変更モード。</summary>
    public GroupPickerWindow(GroupStore store, AvatarGroup group)
    {
        _store = store;
        _renameMode = true;
        InitializeComponent();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        Title = "グループ名を変更";
        SubjectText.Text = $"「{group.Name}」の新しい名前";
        ExistingPanel.Visibility = Visibility.Collapsed;
        NewLabel.Text = "グループ名";
        NameBox.Text = group.Name;
        OkButton.Content = "変更する";
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        UpdateOk();
    }

    private void UpdateOk()
    {
        var hasName = NameBox.Text.Trim().Length > 0;
        OkButton.IsEnabled = hasName || (!_renameMode && GroupList.SelectedItem is not null);
        if (!_renameMode) OkButton.Content = hasName ? "作成して割り当てる" : "割り当てる";
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not null) NameBox.Text = "";
        UpdateOk();
    }

    private void GroupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupList.SelectedItem is not null) Ok_Click(sender, e);
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NameBox.Text.Length > 0 && !_renameMode) GroupList.SelectedItem = null;
        UpdateOk();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OkButton.IsEnabled) Ok_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (_renameMode)
        {
            if (name.Length == 0) return;
            NewName = name;
        }
        else if (name.Length > 0)
        {
            // 同名があればそれを使う
            Result = _store.FindByName(name) ?? _store.Create(name);
        }
        else if (GroupList.SelectedItem is ListBoxItem { Tag: AvatarGroup g })
        {
            Result = g;
        }
        else return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
