using System.Windows;

namespace WinOtp;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        AlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
        MinimizeToTrayCheck.IsChecked = settings.MinimizeToTray;
    }

    public bool AlwaysOnTop => AlwaysOnTopCheck.IsChecked == true;

    public bool MinimizeToTray => MinimizeToTrayCheck.IsChecked == true;

    private void HintsButton_Click(object sender, RoutedEventArgs e)
    {
        HintsPanel.Visibility = HintsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
