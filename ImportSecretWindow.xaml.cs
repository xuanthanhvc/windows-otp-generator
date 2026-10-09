using System.Windows;

namespace WinOtp;

public partial class ImportSecretWindow : Window
{
    public ImportSecretWindow()
    {
        InitializeComponent();
    }

    public string AccountName => NameBox.Text.Trim();

    public string Secret => SecretBox.Text.Trim();

    public int Digits => int.TryParse(DigitsBox.Text.Trim(), out int d) ? d : 6;

    public int Period => int.TryParse(PeriodBox.Text.Trim(), out int p) ? p : 30;

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccountName))
        {
            MessageBox.Show("Enter an account name.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(Secret))
        {
            MessageBox.Show("Enter a Base32 secret.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Totp.Base32Decode(Secret);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Invalid secret:\n{ex.Message}", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Totp.IsSupportedDigits(Digits))
        {
            MessageBox.Show($"Digits must be {Totp.MinDigits}–{Totp.MaxDigits}.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Period <= 0)
        {
            MessageBox.Show("Period must be greater than zero.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
