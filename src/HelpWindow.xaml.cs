using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace VRCAvatarChanger;

public partial class HelpWindow : Window
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCAvatarChanger");

    public HelpWindow(string tab = "howto")
    {
        InitializeComponent();
        SourceInitialized += (_, _) => App.ApplyTitleBarTheme(this);
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"バージョン {ver?.Major}.{ver?.Minor}.{ver?.Build}";
        DataPathText.Text = DataDir;
        (tab switch { "faq" => TabFaq, "about" => TabAbout, _ => TabHowTo }).IsChecked = true;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HowToPanel is null) return;
        HowToPanel.Visibility = TabHowTo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FaqPanel.Visibility = TabFaq.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = TabAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", DataDir) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("データフォルダを開けませんでした", ex); }
    }
}
