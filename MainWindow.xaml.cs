using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZXing;
namespace WinOtp;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AccountRow> _rows = new();
    private readonly List<Account> _accounts = new();
    private readonly CollectionView _rowsView;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly AppSettings _settings;

    private CancellationTokenSource? _clipboardClearCts;
    private string? _lastCopiedOtp;
    private bool _accountsLoadFailed;
    private bool _isSelectionMode;
    private bool _searchOpen;
    private string _searchQuery = "";
    private AccountRow? _dragSource;
    private AccountRow? _renamingRow;
    private string? _renameOriginal;
    private TrayIconHost? _tray;
    private bool _forceExit;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettingsStore.Load();
        ApplyWindowGeometry();
        ApplyAppPreferences();

        _rowsView = (CollectionView)CollectionViewSource.GetDefaultView(_rows);
        _rowsView.Filter = FilterRow;
        AccountsList.ItemsSource = _rowsView;

        AccountStore.EnsureDataDirectory();
        Closing += Window_Closing;

        LoadAccounts();
        ApplyVaultSafetyUi();
        UpdateListChrome();

        _timer.Tick += Timer_Tick;
        _timer.Start();
        Refresh();
    }

    public void RequestShutdown()
    {
        _forceExit = true;
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }

    private bool FilterRow(object item)
    {
        if (item is not AccountRow row)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            return true;
        }

        return row.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<AccountRow> GetVisibleRows() =>
        _rowsView.Cast<AccountRow>().ToList();

    private void UpdateListChrome()
    {
        bool hasAccounts = _rows.Count > 0;
        SearchToggleButton.Visibility = hasAccounts ? Visibility.Visible : Visibility.Collapsed;

        if (!hasAccounts)
        {
            CloseSearch();
        }

        int visible = GetVisibleRows().Count;
        EmptyStateText.Visibility = !hasAccounts ? Visibility.Visible : Visibility.Collapsed;
        NoMatchText.Visibility = hasAccounts && visible == 0 && !string.IsNullOrWhiteSpace(_searchQuery)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccountsList.Visibility = hasAccounts && visible > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Timer_Tick(object? sender, EventArgs e) => Refresh();

    private void ApplyWindowGeometry()
    {
        try
        {
            if (_settings.Width >= MinWidth)
            {
                Width = _settings.Width;
            }

            if (_settings.Height >= MinHeight)
            {
                Height = _settings.Height;
            }

            if (_settings.Left.HasValue && _settings.Top.HasValue &&
                IsOnScreen(_settings.Left.Value, _settings.Top.Value, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.Left.Value;
                Top = _settings.Top.Value;
            }

            if (_settings.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // Corrupt window.json should never stop the app from opening.
        }
    }

    private void ApplyAppPreferences()
    {
        Topmost = _settings.AlwaysOnTop;
        ApplyTrayIcon();
    }

    private void ApplyTrayIcon()
    {
        if (_settings.MinimizeToTray)
        {
            _tray ??= new TrayIconHost(this);
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            _settings.Left = bounds.Left;
            _settings.Top = bounds.Top;
            _settings.Width = bounds.Width;
            _settings.Height = bounds.Height;
            _settings.Maximized = WindowState == WindowState.Maximized;

            AppSettingsStore.Save(_settings);
        }
        catch
        {
            // Non-critical.
        }
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        return left < vRight - 40 && left + width > vLeft + 40 &&
               top < vBottom - 40 && top + height > vTop + 40;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_forceExit && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            SaveAppSettings();
            Hide();
            return;
        }

        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _clipboardClearCts?.Cancel();
        _clipboardClearCts?.Dispose();
        _clipboardClearCts = null;

        ClearClipboardIfHoldsLastOtp();
        SaveAppSettings();
        _tray?.Dispose();
        _tray = null;

        if (!_forceExit)
        {
            Application.Current.Shutdown();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_searchOpen)
            {
                CloseSearch();
                e.Handled = true;
                return;
            }

            if (_renamingRow is not null)
            {
                CancelRename();
                e.Handled = true;
                return;
            }

            if (_isSelectionMode)
            {
                ExitSelectionMode();
                e.Handled = true;
            }

            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            if (_rows.Count > 0)
            {
                OpenSearch();
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control &&
            e.Key is >= Key.D1 and <= Key.D9)
        {
            int index = e.Key - Key.D1;
            CopyVisibleRowAt(index);
            e.Handled = true;
        }
    }

    private void CopyVisibleRowAt(int zeroBasedIndex)
    {
        IReadOnlyList<AccountRow> visible = GetVisibleRows();
        if (zeroBasedIndex < 0 || zeroBasedIndex >= visible.Count)
        {
            return;
        }

        CopyCode(visible[zeroBasedIndex]);
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_searchOpen)
        {
            CloseSearch();
        }
        else
        {
            OpenSearch();
        }
    }

    private void OpenSearch()
    {
        _searchOpen = true;
        SearchBox.Visibility = Visibility.Visible;
        SearchToggleButton.FontWeight = FontWeights.Bold;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        _searchOpen = false;
        _searchQuery = "";
        SearchBox.Text = "";
        SearchBox.Visibility = Visibility.Collapsed;
        SearchToggleButton.FontWeight = FontWeights.Normal;
        _rowsView.Refresh();
        UpdateListChrome();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            _searchQuery = box.Text;
            _rowsView.Refresh();
            UpdateListChrome();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings.AlwaysOnTop = dialog.AlwaysOnTop;
            _settings.MinimizeToTray = dialog.MinimizeToTray;
            Topmost = _settings.AlwaysOnTop;
            ApplyTrayIcon();
            SaveAppSettings();
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportButton.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = ImportButton;
            menu.IsOpen = true;
        }
    }

    private void ImportQr_Click(object sender, RoutedEventArgs e)
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
                MessageBox.Show("No QR code was found in the selected image.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Account? account = ParseOtpAuth(qrText);
            if (account is null)
            {
                MessageBox.Show(
                    "The QR code does not contain a valid otpauth:// TOTP entry.",
                    "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Totp.Base32Decode(account.Secret);
            TryAddAccount(account);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import QR code:\n{ex.Message}", "Import",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSecret_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureVaultWritable())
        {
            return;
        }

        var dialog = new ImportSecretWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var account = new Account
        {
            Name = dialog.AccountName,
            Secret = dialog.Secret,
            Digits = dialog.Digits,
            Period = dialog.Period,
            Algorithm = "SHA1"
        };

        try
        {
            Totp.Base32Decode(account.Secret);
            TryAddAccount(account);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import secret:\n{ex.Message}", "Import",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TryAddAccount(Account account)
    {
        if (HasDuplicateSecret(account.Secret))
        {
            MessageBoxResult dup = MessageBox.Show(
                $"An account with the same secret already exists.\n\nImport '{account.Name}' anyway?",
                "Import",
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
                "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Refresh();
        UpdateListChrome();
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
        ImportButton.IsEnabled = !_accountsLoadFailed && !_isSelectionMode;
        DeleteButton.IsEnabled = !_accountsLoadFailed;
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
        if (sender is Button { Tag: AccountRow row })
        {
            CopyCode(row);
        }
    }

    private void Code_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AccountRow row })
        {
            CopyCode(row);
            e.Handled = true;
        }
    }

    private void CopyCode(AccountRow row)
    {
        if (string.IsNullOrEmpty(row.RawCode))
        {
            return;
        }

        try
        {
            string code = row.RawCode;
            Clipboard.SetText(code);
            _lastCopiedOtp = code;
            ScheduleClipboardClear(code);
            row.ShowCopiedFeedback();
        }
        catch
        {
            // Clipboard can occasionally be locked by another process; ignore.
        }
    }

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

        if (!_isSelectionMode)
        {
            EnterSelectionMode();
            return;
        }

        DeleteRows(_rows.Where(r => r.IsSelected).ToList());
    }

    private void RowDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureVaultWritable())
        {
            return;
        }

        if (sender is MenuItem { Tag: AccountRow row })
        {
            DeleteRows(new List<AccountRow> { row });
        }
    }

    private void DeleteRows(List<AccountRow> selected)
    {
        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Select at least one account to delete.",
                "Delete accounts",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string message = selected.Count == 1
            ? $"Delete account '{selected[0].Name}'?"
            : $"Delete {selected.Count} selected accounts?";

        if (MessageBox.Show(message, "Delete accounts", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = selected
            .Select(row => (
                Row: row,
                Account: row.Account,
                AccountIndex: _accounts.IndexOf(row.Account),
                RowIndex: _rows.IndexOf(row)))
            .OrderByDescending(x => x.RowIndex)
            .ToList();

        foreach (var item in removed)
        {
            _accounts.Remove(item.Account);
            _rows.Remove(item.Row);
        }

        if (!SaveAccounts())
        {
            foreach (var item in removed.OrderBy(x => x.RowIndex))
            {
                if (item.AccountIndex >= 0 && item.AccountIndex <= _accounts.Count)
                {
                    _accounts.Insert(item.AccountIndex, item.Account);
                }
                else
                {
                    _accounts.Add(item.Account);
                }

                if (item.RowIndex >= 0 && item.RowIndex <= _rows.Count)
                {
                    _rows.Insert(item.RowIndex, item.Row);
                }
                else
                {
                    _rows.Add(item.Row);
                }
            }

            MessageBox.Show(
                "Could not save after delete. The accounts were restored in the list. " +
                "Your vault on disk was not changed.",
                "WinOTP", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateListChrome();
            return;
        }

        ExitSelectionMode();
        UpdateListChrome();
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e) => ExitSelectionMode();

    private void EnterSelectionMode()
    {
        CancelRename();
        _isSelectionMode = true;
        foreach (AccountRow row in _rows)
        {
            row.IsSelected = false;
            row.IsSelectionMode = true;
        }

        CancelDeleteButton.Visibility = Visibility.Visible;
        ApplyVaultSafetyUi();
    }

    private void ExitSelectionMode()
    {
        _isSelectionMode = false;
        foreach (AccountRow row in _rows)
        {
            row.IsSelected = false;
            row.IsSelectionMode = false;
        }

        CancelDeleteButton.Visibility = Visibility.Collapsed;
        ApplyVaultSafetyUi();
    }

    private void RenameMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: AccountRow row })
        {
            BeginRename(row);
        }
    }

    private void BeginRename(AccountRow row)
    {
        if (!EnsureVaultWritable() || _isSelectionMode)
        {
            return;
        }

        CancelRename();
        _renamingRow = row;
        _renameOriginal = row.Name;
        row.IsRenaming = true;
        // Focus happens in RenameBox_IsVisibleChanged once the TextBox is shown.
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || box.Visibility != Visibility.Visible)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!box.IsVisible)
            {
                return;
            }

            box.Focus();
            box.SelectAll();
        });
    }

    private void CommitRename(AccountRow row)
    {
        row.IsRenaming = false;
        if (ReferenceEquals(_renamingRow, row))
        {
            _renamingRow = null;
            _renameOriginal = null;
        }

        if (string.IsNullOrWhiteSpace(row.Name))
        {
            MessageBox.Show("Account name cannot be empty.", "Rename",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_renameOriginal is not null)
            {
                row.Name = _renameOriginal;
            }

            _renamingRow = null;
            _renameOriginal = null;
            return;
        }

        row.Name = row.Name.Trim();
        SaveAccounts();
    }

    private void CancelRename()
    {
        if (_renamingRow is null)
        {
            return;
        }

        _renamingRow.IsRenaming = false;
        if (_renameOriginal is not null)
        {
            _renamingRow.Name = _renameOriginal;
        }

        _renamingRow = null;
        _renameOriginal = null;
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: AccountRow row })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitRename(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: AccountRow row } && row.IsRenaming)
        {
            CommitRename(row);
        }
    }

    private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectionMode)
        {
            return;
        }

        if (sender is FrameworkElement { Tag: AccountRow row })
        {
            _dragSource = row;
        }
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (sender is not FrameworkElement fe || fe.Tag is not AccountRow row || !ReferenceEquals(row, _dragSource))
        {
            return;
        }

        DragDrop.DoDragDrop(fe, row, DragDropEffects.Move);
        _dragSource = null;
    }

    private void AccountsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AccountRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void AccountsList_Drop(object sender, DragEventArgs e)
    {
        if (!EnsureVaultWritable())
        {
            return;
        }

        if (e.Data.GetData(typeof(AccountRow)) is not AccountRow source)
        {
            return;
        }

        Point pos = e.GetPosition(AccountsList);
        HitTestResult? hit = VisualTreeHelper.HitTest(AccountsList, pos);
        DependencyObject? current = hit?.VisualHit;
        while (current is not null and not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is not ListBoxItem { DataContext: AccountRow target })
        {
            return;
        }

        ReorderRows(source, target);
    }

    private void ReorderRows(AccountRow source, AccountRow target)
    {
        int oldIndex = _rows.IndexOf(source);
        int newIndex = _rows.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        _rows.Move(oldIndex, newIndex);
        Account account = _accounts[oldIndex];
        _accounts.RemoveAt(oldIndex);
        _accounts.Insert(newIndex, account);
        SaveAccounts();
    }

    private bool HasDuplicateSecret(string secret)
    {
        string normalized = NormalizeSecretKey(secret);
        return _accounts.Any(a =>
            string.Equals(NormalizeSecretKey(a.Secret), normalized, StringComparison.Ordinal));
    }

    private static string NormalizeSecretKey(string secret) =>
        secret.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();

    private static string? DecodeQr(string imagePath)
    {
        BitmapImage source = new();
        using (FileStream stream = File.OpenRead(imagePath))
        {
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream;
            source.EndInit();
        }

        FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);
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

        if (!string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("secret", out string? secret) || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

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

    private static string BuildName(string? issuer, string label)
    {
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

public sealed class Config
{
    [JsonPropertyName("accounts")]
    public List<Account>? Accounts { get; set; }
}

public sealed class Account
{
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public string Algorithm { get; set; } = "SHA1";
}

public sealed class AccountRow : INotifyPropertyChanged
{
    private static readonly Brush CountdownNormalBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush CountdownWarnBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x6C, 0x00));
    private static readonly Brush CountdownUrgentBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

    private readonly Account _account;
    private string _code = "";
    private string _countdownText = "";
    private int _remainingSeconds = -1;
    private bool _isSelected;
    private bool _isSelectionMode;
    private bool _isRenaming;
    private string _copyButtonText = "Copy";
    private Brush _countdownBrush = CountdownNormalBrush;
    private CancellationTokenSource? _copiedFeedbackCts;

    static AccountRow()
    {
        CountdownNormalBrush.Freeze();
        CountdownWarnBrush.Freeze();
        CountdownUrgentBrush.Freeze();
    }

    public AccountRow(Account account)
    {
        _account = account;
    }

    public Account Account => _account;

    public string Name
    {
        get => _account.Name;
        set
        {
            if (_account.Name == value)
            {
                return;
            }

            _account.Name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    public void RefreshNameFromAccount() => OnPropertyChanged(nameof(Name));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (_isSelectionMode == value)
            {
                return;
            }

            _isSelectionMode = value;
            OnPropertyChanged(nameof(IsSelectionMode));
            OnPropertyChanged(nameof(ShowDragHandle));
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value)
            {
                return;
            }

            _isRenaming = value;
            OnPropertyChanged(nameof(IsRenaming));
        }
    }

    public bool ShowDragHandle => !_isSelectionMode;

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

    public Brush CountdownBrush
    {
        get => _countdownBrush;
        private set
        {
            if (ReferenceEquals(_countdownBrush, value))
            {
                return;
            }

            _countdownBrush = value;
            OnPropertyChanged(nameof(CountdownBrush));
        }
    }

    public string CopyButtonText
    {
        get => _copyButtonText;
        private set
        {
            if (_copyButtonText == value)
            {
                return;
            }

            _copyButtonText = value;
            OnPropertyChanged(nameof(CopyButtonText));
        }
    }

    public int RemainingSeconds
    {
        get => _remainingSeconds;
        private set
        {
            if (_remainingSeconds == value)
            {
                return;
            }

            _remainingSeconds = value;
            OnPropertyChanged(nameof(RemainingSeconds));
            CountdownBrush = value switch
            {
                <= 5 => CountdownUrgentBrush,
                <= 10 => CountdownWarnBrush,
                _ => CountdownNormalBrush
            };
        }
    }

    public void ShowCopiedFeedback()
    {
        _copiedFeedbackCts?.Cancel();
        _copiedFeedbackCts?.Dispose();
        _copiedFeedbackCts = new CancellationTokenSource();
        CancellationToken token = _copiedFeedbackCts.Token;

        CopyButtonText = "Copied!";
        _ = ResetCopyFeedbackAsync(token);
    }

    private async Task ResetCopyFeedbackAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(1200, token);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    CopyButtonText = "Copy";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // A newer copy superseded this feedback.
        }
    }

    public void Update()
    {
        if (!Totp.IsSupportedDigits(_account.Digits))
        {
            RawCode = "";
            Code = new string('-', Totp.MinDigits);
            RemainingSeconds = 0;
            CountdownText = "unsupported";
            return;
        }

        try
        {
            var (code, secondsRemaining) = Totp.Generate(
                _account.Secret, _account.Digits, _account.Period, _account.Algorithm);

            RawCode = code;
            Code = Group(code);
            RemainingSeconds = secondsRemaining;
            CountdownText = $"{secondsRemaining}s";
        }
        catch
        {
            RawCode = "";
            Code = new string('-', Totp.MinDigits);
            RemainingSeconds = 0;
            CountdownText = "invalid";
        }
    }

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
