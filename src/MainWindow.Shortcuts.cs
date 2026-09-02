using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace VRCAvatarChanger;

// メイン画面のキーボード操作。
//
// 一覧を選んでいるときは Enter で着替え、Ctrl+F で検索、Ctrl+1〜3 でタブ、といった
// Windows アプリの標準的な組み合わせに寄せている。VRChat 中のホットキー (MainWindow.Hotkeys.cs) とは別物で、
// こちらはこのウィンドウが手前のときだけ効く。
//
// 入力欄 (検索欄・ID 欄) に文字を打っている最中は、Enter / Delete / Backspace / Ctrl+C を横取りしない。
public partial class MainWindow
{
    /// <summary>一覧が使える状態か (メイン画面が出ていて、設定や詳細が重なっていない)。</summary>
    private bool ListActive
        => MainPanel.Visibility == Visibility.Visible
           && SettingsOverlay.Visibility != Visibility.Visible
           && DetailOverlay.Visibility != Visibility.Visible;

    /// <summary>文字入力欄にフォーカスがあるか。このときは文字の編集に使うキーを奪わない。</summary>
    private static bool TypingInTextBox => Keyboard.FocusedElement is TextBoxBase or System.Windows.Controls.PasswordBox;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt を押している間、文字キーは Key.System として届く
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key == Key.F1) { e.Handled = true; ShowHelp(); return; }

        // Esc: 重なっているものから順に閉じる → 検索を消す → グループから戻る
        if (key == Key.Escape)
        {
            if (DetailOverlay.Visibility == Visibility.Visible) { e.Handled = true; CloseDetail(); return; }
            if (SettingsOverlay.Visibility == Visibility.Visible) { e.Handled = true; CloseSettings(); return; }
            if (MainPanel.Visibility != Visibility.Visible) return;
            if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
            {
                e.Handled = true;
                SearchBox.Clear();
                FocusList();
                return;
            }
            if (_openGroup is not null) { e.Handled = true; CloseGroup(); return; }
            return;
        }

        if (MainPanel.Visibility != Visibility.Visible) return;

        // 設定 (Ctrl+,) と使い方はどの状態でも
        if (key == Key.OemComma && mods == ModifierKeys.Control)
        {
            e.Handled = true;
            if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings(); else { CloseDetail(); OpenSettings(); }
            return;
        }

        if (!ListActive) return;

        // ---- 修飾キーつき ----
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.F: e.Handled = true; FocusSearch(); return;
                case Key.D1 or Key.NumPad1: e.Handled = true; SourceOwn.IsChecked = true; FocusList(); return;
                case Key.D2 or Key.NumPad2: e.Handled = true; SourceFavorites.IsChecked = true; FocusList(); return;
                case Key.D3 or Key.NumPad3: e.Handled = true; SourcePublic.IsChecked = true; FocusList(); return;
                case Key.Tab: e.Handled = true; CycleSource(+1); return;
                case Key.G: e.Handled = true; GroupToggle.IsChecked = GroupToggle.IsChecked != true; return;
                case Key.I: e.Handled = true; ShowDetailOfSelection(); return;
                case Key.C when !TypingInTextBox: e.Handled = true; MenuCopyId_Click(sender, e); return;
            }
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.Tab: e.Handled = true; CycleSource(-1); return;
                case Key.L: e.Handled = true; (IsGridView ? ViewList : ViewGrid).IsChecked = true; return;
                case Key.C: e.Handled = true; ToggleStripeColors(); return; // 隠し機能: 10 体ごとの色分け
            }
        }
        if (mods == ModifierKeys.Alt && key == Key.Enter) { e.Handled = true; ShowDetailOfSelection(); return; }

        if (key == Key.F5 && RefreshButton.IsEnabled) { e.Handled = true; RefreshButton_Click(sender, e); return; }

        // ---- 修飾キーなし。入力欄で打っている最中は奪わない ----
        if (mods != ModifierKeys.None) return;
        if (TypingInTextBox)
        {
            // 検索欄で Enter: 絞り込んだ先頭を選んで一覧へ (そのままもう一度 Enter で着替えられる)
            if (key == Key.Enter && SearchBox.IsKeyboardFocusWithin) { e.Handled = true; FocusList(); }
            return;
        }
        switch (key)
        {
            case Key.Enter:
                if (AvatarList.SelectedItem is AvatarItem sel)
                {
                    e.Handled = true;
                    if (sel.IsGroup) OpenGroup(sel.Group!);
                    else _ = ChangeFromListAsync(sel);
                }
                return;
            case Key.Back:
                if (_openGroup is not null) { e.Handled = true; CloseGroup(); }
                return;
            case Key.Delete:
                if (IsPublicTab && AvatarList.SelectedItem is AvatarItem { IsAvatar: true }) { e.Handled = true; _ = RemoveSelectedFromPublicAsync(); }
                return;
        }
    }

    /// <summary>検索欄へ。すでに入っている文字は全選択にして、打ち直せるようにする。</summary>
    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>一覧へフォーカスを戻す。何も選んでいなければ先頭を選ぶ (矢印キーですぐ動かせるように)。</summary>
    private void FocusList()
    {
        if (AvatarList.Items.Count == 0) { AvatarList.Focus(); return; }
        if (AvatarList.SelectedItem is null) AvatarList.SelectedIndex = 0;
        // 選択中の行そのものにフォーカスを置く (ListView 本体だと矢印キーが 1 回目に効かないことがある)
        AvatarList.UpdateLayout();
        AvatarList.ScrollIntoView(AvatarList.SelectedItem);
        if (AvatarList.ItemContainerGenerator.ContainerFromItem(AvatarList.SelectedItem) is ListViewItem row) row.Focus();
        else AvatarList.Focus();
    }

    /// <summary>タブを順送り (自分 → お気に入り → パブリック → 自分...)。</summary>
    private void CycleSource(int step)
    {
        RadioButton[] tabs = [SourceOwn, SourceFavorites, SourcePublic];
        var at = Array.FindIndex(tabs, t => t.IsChecked == true);
        tabs[((at < 0 ? 0 : at) + step + tabs.Length) % tabs.Length].IsChecked = true;
        FocusList();
    }

    private void ShowDetailOfSelection()
    {
        if (AvatarList.SelectedItem is AvatarItem { IsAvatar: true } item) OpenDetail(item);
    }

    /// <summary>Delete キーからの削除。メニューと違ってキー 1 つで消えてしまうので、一度だけ確かめる。</summary>
    private async Task RemoveSelectedFromPublicAsync()
    {
        if (AvatarList.SelectedItem is not AvatarItem { IsAvatar: true } item) return;
        var r = MessageBox.Show(this, $"{item.Name} をパブリックから外しますか?\n(もう一度 ID か URL で追加すれば戻せます)",
            "VRCAvatarChanger", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        var index = AvatarList.SelectedIndex;
        _public.Remove(item.Id);
        SetStatus(StatusKind.Info, $"{item.Name} をパブリックから外しました");
        await LoadAvatarsAsync();
        // 消した位置の次を選んでおくと、続けて Delete で片付けられる
        if (AvatarList.Items.Count > 0) AvatarList.SelectedIndex = Math.Min(index, AvatarList.Items.Count - 1);
        FocusList();
    }
}
