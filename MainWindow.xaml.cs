using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZXing;

namespace WinOtp;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AccountRow> _rows = new();
    private readonly List<Account> _accounts = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _clipboardClearCts;
    private string? _lastCopiedOtp;
    private bool _accountsLoadFailed;

    public MainWindow()
    {
        InitializeComponent();
        AccountsList.ItemsSource = _rows;

        AccountStore.EnsureDataDirectory();
        RestoreWindowBounds();
        Closing += Window_Closing;

        LoadAccounts();
        ApplyVaultSafetyUi();

        _timer.Tick += Timer_Tick;
        _timer.Start();
        Refresh();
    }

    private void Timer_Tick(object? sender, EventArgs e) => Refresh();

    /// <summary>Restores the saved window size, position and maximized state, if any.</summary>
    private void RestoreWindowBounds()
    {
        try
        {
            // EnsureDataDirectory migrates window.json from beside the EXE when needed.
            string path = AccountStore.WindowStatePath;
            if (!File.Exists(path) && File.Exists(AccountStore.PortableWindowStatePath))
            {
                path = AccountStore.PortableWindowStatePath;
            }

            if (!File.Exists(path))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(path));
            if (saved is null)
            {
                return;
            }

            // Only apply a size that respects the window's minimums.
            if (saved.Width >= MinWidth)
            {
                Width = saved.Width;
            }
            if (saved.Height >= MinHeight)
            {
                Height = saved.Height;
            }

            // Restore position only if it lands within the visible virtual screen.
            if (saved.Left.HasValue && saved.Top.HasValue &&
                IsOnScreen(saved.Left.Value, saved.Top.Value, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = saved.Left.Value;
                Top = saved.Top.Value;
            }

            if (saved.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // A corrupt window.json should never stop the app from opening.
        }
    }

    /// <summary>True if the given rectangle is at least partially on a screen.</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        // Require a reasonable overlap so the title bar stays grabbable.
        return left < vRight - 40 && left + width > vLeft + 40 &&
               top < vBottom - 40 && top + height > vTop + 40;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _clipboardClearCts?.Cancel();
        _clipboardClearCts?.Dispose();
        _clipboardClearCts = null;

        ClearClipboardIfHoldsLastOtp();

        try
        {
            // RestoreBounds holds the normal (un-maximized) size/position even when maximized.
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            var state = new WindowBounds
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized
            };

            AccountStore.EnsureDataDirectory();
            AtomicFile.WriteAllText(
                AccountStore.WindowStatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Failing to save window state must not block closing.
        }
    }

    private void LoadAccounts()
    {
        try
        {
            LoadResult result = AccountStore.Load();

            if (!string.IsNullOrEmpty(result.LegacyMessage))
            {
                MessageBox.Show(result.LegacyMessage, "WinOTP",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Config? config = result.Config;
            if (config?.Accounts is null || config.Accounts.Count == 0)
            {
                return;
            }

            var invalidDigits = new List<string>();
            foreach (Account account in config.Accounts)
            {
                // Keep invalid-digit accounts in memory so a later save cannot drop them,
                // but surface a warning and let the row show an error instead of wrong codes.
                if (!Totp.IsSupportedDigits(account.Digits))
                {
                    invalidDigits.Add($"'{account.Name}' (digits={account.Digits})");
                }

                try
                {
                    account.Algorithm = Totp.NormalizeAlgorithm(account.Algorithm);
                }
                catch (ArgumentException)
                {
                    // Leave as stored; row.Update surfaces the error.
                }

                _accounts.Add(account);
                _rows.Add(new AccountRow(account));
            }

            if (invalidDigits.Count > 0)
            {
                MessageBox.Show(
                    "Account(s) with unsupported digits (must be 6–8) will not show codes until fixed:\n" +
                    string.Join("\n", invalidDigits),
                    "WinOTP", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _accountsLoadFailed = true;
            MessageBox.Show(
                $"Failed to read accounts:\n{ex.Message}\n\n" +
                "Import and Delete are disabled so a failed load cannot overwrite your vault. " +
                "Fix or restore accounts.dat (a .bak may exist under LocalAppData\\WinOtp), then restart.",
                "WinOTP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyVaultSafetyUi()
    {
        ImportButton.IsEnabled = !_accountsLoadFailed;
    }

    private bool EnsureVaultWritable()
    {
        if (!_accountsLoadFailed)
        {
            return true;
        }

        MessageBox.Show(
            "Accounts failed to load. Saving, importing, and deleting are blocked " +
            "to avoid overwriting your vault. Restore accounts.dat (or accounts.dat.bak) and restart.",
            "WinOTP", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    private void Refresh()
    {
        foreach (AccountRow row in _rows)
        {
            row.Update();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AccountRow row } && !string.IsNullOrEmpty(row.RawCode))
        {
            try
            {
                string code = row.RawCode;
                Clipboard.SetText(code);
                _lastCopiedOtp = code;
                ScheduleClipboardClear(code);
            }
            catch
            {
                // Clipboard can occasionally be locked by another process; ignore.
            }
        }
    }

    /// <summary>
    /// Clears the clipboard after ~30s only if it still holds the copied OTP.
    /// </summary>
    private void ScheduleClipboardClear(string code)
    {
        _clipboardClearCts?.Cancel();
        _clipboardClearCts?.Dispose();
        _clipboardClearCts = new CancellationTokenSource();
        CancellationToken token = _clipboardClearCts.Token;

        _ = ClearClipboardLaterAsync(code, token);
    }

    private async Task ClearClipboardLaterAsync(string code, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (Clipboard.ContainsText() && Clipboard.GetText() == code)
                    {
                        Clipboard.Clear();
                    }

                    if (_lastCopiedOtp == code)
                    {
                        _lastCopiedOtp = null;
                    }
                }
                catch
                {
                    // Clipboard can be locked; ignore.
                }
            });
        }
        catch (OperationCanceledException)
        {
            // A newer copy replaced this timer, or the window is closing.
        }
    }

    private void ClearClipboardIfHoldsLastOtp()
    {
        if (string.IsNullOrEmpty(_lastCopiedOtp))
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _lastCopiedOtp)
            {
                Clipboard.Clear();
            }
        }
        catch
        {
            // Clipboard can be locked; ignore.
        }
        finally
        {
            _lastCopiedOtp = null;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureVaultWritable())
        {
            return;
        }

        AccountRow? row = sender switch
        {
            MenuItem { Tag: AccountRow r } => r,
            Button { Tag: AccountRow r } => r,
            _ => null
        };

        if (row is null)
        {
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            $"Delete account '{row.Name}'?",
            "Delete account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        Account account = row.Account;
        int accountIndex = _accounts.IndexOf(account);
        int rowIndex = _rows.IndexOf(row);

        _accounts.Remove(account);
        _rows.Remove(row);

        if (!SaveAccounts())
        {
            // Restore so UI stays in sync with the vault on disk.
            if (accountIndex >= 0 && accountIndex <= _accounts.Count)
            {
                _accounts.Insert(accountIndex, account);
            }
            else
            {
                _accounts.Add(account);
            }

            if (rowIndex >= 0 && rowIndex <= _rows.Count)
            {
                _rows.Insert(rowIndex, row);
            }
            else
            {
                _rows.Add(row);
            }

            MessageBox.Show(
                "Could not save after delete. The account was restored in the list. " +
                "Your vault on disk was not changed.",
                "WinOTP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureVaultWritable())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import QR code image",
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string? qrText = DecodeQr(dialog.FileName);
            if (string.IsNullOrWhiteSpace(qrText))
            {
                MessageBox.Show("No QR code was found in the selected image.", "Import QR",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Account? account = ParseOtpAuth(qrText);
            if (account is null)
            {
                MessageBox.Show(
                    "The QR code does not contain a valid otpauth:// TOTP entry.",
                    "Import QR", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate the secret decodes before accepting it.
            Totp.Base32Decode(account.Secret);

            if (HasDuplicateSecret(account.Secret))
            {
                MessageBoxResult dup = MessageBox.Show(
                    $"An account with the same secret already exists.\n\nImport '{account.Name}' anyway?",
                    "Import QR",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (dup != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _accounts.Add(account);
            var row = new AccountRow(account);
            _rows.Add(row);

            if (!SaveAccounts())
            {
                _accounts.Remove(account);
                _rows.Remove(row);
                MessageBox.Show(
                    "Import succeeded in memory but saving the vault failed. " +
                    "The new account was rolled back and was not kept.",
                    "Import QR", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Refresh();

            MessageBox.Show($"Imported '{account.Name}'.", "Import QR",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import QR code:\n{ex.Message}", "Import QR",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool HasDuplicateSecret(string secret)
    {
        string normalized = NormalizeSecretKey(secret);
        return _accounts.Any(a =>
            string.Equals(NormalizeSecretKey(a.Secret), normalized, StringComparison.Ordinal));
    }

    private static string NormalizeSecretKey(string secret) =>
        secret.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();

    /// <summary>Decodes the first QR/barcode found in an image file. Returns null if none.</summary>
    private static string? DecodeQr(string imagePath)
    {
        // Load the image via WPF imaging (handles PNG and JPG natively, no System.Drawing).
        BitmapImage source = new();
        using (FileStream stream = File.OpenRead(imagePath))
        {
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream;
            source.EndInit();
        }

        // Convert to a known 32bpp BGRA format so we can hand raw bytes to ZXing.
        FormatConvertedBitmap converted = new(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        var luminance = new RGBLuminanceSource(pixels, width, height,
            RGBLuminanceSource.BitmapFormat.BGRA32);
        Result? result = reader.Decode(luminance);
        return result?.Text;
    }

    /// <summary>
    /// Parses an otpauth://totp/Label?secret=...&amp;issuer=...&amp;digits=...&amp;period=...&amp;algorithm=...
    /// URI into an Account. Returns null if it is not a valid TOTP URI.
    /// Throws if digits are present but outside the supported 6–8 range.
    /// </summary>
    private static Account? ParseOtpAuth(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, "otpauth", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Only time-based OTP is supported by this tool.
        if (!string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("secret", out string? secret) || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        // Label is the path, often "Issuer:account@example.com".
        string label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        query.TryGetValue("issuer", out string? issuer);

        string name = BuildName(issuer, label);

        query.TryGetValue("algorithm", out string? algorithm);
        var account = new Account
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Imported" : name,
            Secret = secret.Trim(),
            Algorithm = Totp.NormalizeAlgorithm(algorithm)
        };

        if (query.TryGetValue("digits", out string? digitsText) &&
            int.TryParse(digitsText, out int digits) && digits > 0)
        {
            if (!Totp.IsSupportedDigits(digits))
            {
                throw new ArgumentException(
                    $"Unsupported digits value '{digits}'. Use {Totp.MinDigits}–{Totp.MaxDigits}.");
            }

            account.Digits = digits;
        }

        if (query.TryGetValue("period", out string? periodText) &&
            int.TryParse(periodText, out int period) && period > 0)
        {
            account.Period = period;
        }

        return account;
    }

    /// <summary>Minimal query-string parser (avoids System.Web dependency).</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        string q = query[0] == '?' ? query[1..] : query;
        foreach (string pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair[..eq] : pair;
            string value = eq >= 0 ? pair[(eq + 1)..] : "";
            key = Uri.UnescapeDataString(key.Replace("+", " "));
            value = Uri.UnescapeDataString(value.Replace("+", " "));
            if (!string.IsNullOrEmpty(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Builds a friendly display name from the issuer parameter and the QR label.</summary>
    private static string BuildName(string? issuer, string label)
    {
        // Label may already be "Issuer:account". Split it out.
        string labelIssuer = "";
        string labelAccount = label;
        int colon = label.IndexOf(':');
        if (colon >= 0)
        {
            labelIssuer = label[..colon].Trim();
            labelAccount = label[(colon + 1)..].Trim();
        }

        string finalIssuer = !string.IsNullOrWhiteSpace(issuer) ? issuer! : labelIssuer;

        if (!string.IsNullOrWhiteSpace(finalIssuer) && !string.IsNullOrWhiteSpace(labelAccount))
        {
            return $"{finalIssuer} ({labelAccount})";
        }
        if (!string.IsNullOrWhiteSpace(finalIssuer))
        {
            return finalIssuer;
        }
        return labelAccount;
    }

    /// <summary>
    /// Persists the current account list to the encrypted LocalAppData store.
    /// Returns false when the vault is locked after a failed load, or when the write fails.
    /// </summary>
    private bool SaveAccounts()
    {
        if (_accountsLoadFailed)
        {
            MessageBox.Show(
                "Accounts failed to load earlier. Refusing to save so the vault on disk is not overwritten.",
                "WinOTP", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        try
        {
            var config = new Config { Accounts = _accounts };
            AccountStore.Save(config);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save accounts:\n{ex.Message}",
                "WinOTP", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}

/// <summary>Root of the accounts config document.</summary>
public sealed class Config
{
    [JsonPropertyName("accounts")]
    public List<Account>? Accounts { get; set; }
}

/// <summary>Persisted window size, position and maximized state (window.json).</summary>
public sealed class WindowBounds
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>A single configured account.</summary>
public sealed class Account
{
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public string Algorithm { get; set; } = "SHA1";
}

/// <summary>Display row with live-updating code and countdown.</summary>
public sealed class AccountRow : INotifyPropertyChanged
{
    private readonly Account _account;
    private string _code = "";
    private string _countdownText = "";
    private int _remainingSeconds = -1;

    public AccountRow(Account account)
    {
        _account = account;
    }

    public Account Account => _account;

    public string Name => _account.Name;

    /// <summary>Code as displayed, grouped for readability (e.g. "492 810").</summary>
    public string Code
    {
        get => _code;
        private set
        {
            if (_code == value)
            {
                return;
            }

            _code = value;
            OnPropertyChanged(nameof(Code));
        }
    }

    /// <summary>Code as digits only, for clipboard.</summary>
    public string RawCode { get; private set; } = "";

    public string CountdownText
    {
        get => _countdownText;
        private set
        {
            if (_countdownText == value)
            {
                return;
            }

            _countdownText = value;
            OnPropertyChanged(nameof(CountdownText));
        }
    }

    /// <summary>Seconds left in the current TOTP step (always refreshed each tick).</summary>
    public int RemainingSeconds
    {
        get => _remainingSeconds;
        private set
        {
            _remainingSeconds = value;
            OnPropertyChanged(nameof(RemainingSeconds));
        }
    }

    public void Update()
    {
        if (!Totp.IsSupportedDigits(_account.Digits))
        {
            RawCode = "";
            Code = new string('-', Totp.MinDigits);
            RemainingSeconds = 0;
            CountdownText = "(unsupported digits)";
            return;
        }

        try
        {
            var (code, secondsRemaining) = Totp.Generate(
                _account.Secret, _account.Digits, _account.Period, _account.Algorithm);

            RawCode = code;
            Code = Group(code);
            RemainingSeconds = secondsRemaining;
            CountdownText = $"({secondsRemaining}s left)";
        }
        catch
        {
            RawCode = "";
            Code = new string('-', Totp.MinDigits);
            RemainingSeconds = 0;
            CountdownText = "(invalid secret)";
        }
    }

    /// <summary>Groups a code into two halves for display, e.g. "492810" -> "492 810".</summary>
    private static string Group(string code)
    {
        if (code.Length == 6)
        {
            return $"{code[..3]} {code[3..]}";
        }
        if (code.Length == 8)
        {
            return $"{code[..4]} {code[4..]}";
        }
        return code;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
