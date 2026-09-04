using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinOtp;

/// <summary>
/// Persists accounts under %LocalAppData%\WinOtp\ as a DPAPI-protected file.
/// Falls back to (and migrates from) an encrypted vault beside the EXE, and
/// migrates leftover plaintext accounts.json from the app directory whenever present.
/// </summary>
internal static class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Preferred data directory: %LocalAppData%\WinOtp\</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinOtp");

    /// <summary>Folder containing the running EXE (portable / legacy location).</summary>
    public static string AppDirectory { get; } = AppContext.BaseDirectory;

    public static string AccountsPath => Path.Combine(DataDirectory, "accounts.dat");

    public static string AccountsBackupPath => AccountsPath + ".bak";

    /// <summary>Encrypted vault beside the EXE (read/migrate only; saves go to LocalAppData).</summary>
    public static string PortableAccountsPath => Path.Combine(AppDirectory, "accounts.dat");

    public static string PortableAccountsBackupPath => PortableAccountsPath + ".bak";

    public static string WindowStatePath => Path.Combine(DataDirectory, "window.json");

    public static string PortableWindowStatePath => Path.Combine(AppDirectory, "window.json");

    /// <summary>Legacy plaintext next to the EXE.</summary>
    public static string LegacyAccountsPath => Path.Combine(AppDirectory, "accounts.json");

    /// <summary>Alias for <see cref="PortableWindowStatePath"/> (older call sites).</summary>
    public static string LegacyWindowStatePath => PortableWindowStatePath;

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        MigrateWindowStateIfNeeded();
    }

    /// <summary>
    /// Loads accounts from LocalAppData, falling back to / migrating from the EXE folder.
    /// </summary>
    public static LoadResult Load()
    {
        EnsureDataDirectory();

        Config? config = null;
        if (File.Exists(AccountsPath))
        {
            config = ReadEncrypted(AccountsPath);
        }

        var messages = new List<string>();

        if (File.Exists(PortableAccountsPath) &&
            !PathsEqual(PortableAccountsPath, AccountsPath))
        {
            try
            {
                string? portableMsg = HandlePortableEncrypted(ref config);
                if (!string.IsNullOrEmpty(portableMsg))
                {
                    messages.Add(portableMsg);
                }
            }
            catch (Exception ex) when (config?.Accounts is { Count: > 0 })
            {
                // LocalAppData vault is usable; don't fail startup over a bad EXE-folder file.
                messages.Add(
                    $"Could not migrate encrypted accounts.dat beside the EXE:\n{ex.Message}\n\n" +
                    $"Your LocalAppData vault was left unchanged:\n{AccountsPath}\n\n" +
                    $"Fix or remove:\n{PortableAccountsPath}");
            }
        }

        if (File.Exists(LegacyAccountsPath))
        {
            string plaintextMsg = HandleLegacyPlaintext(ref config);
            if (!string.IsNullOrEmpty(plaintextMsg))
            {
                messages.Add(plaintextMsg);
            }
        }

        string? combined = messages.Count == 0 ? null : string.Join("\n\n", messages);
        return new LoadResult(config, combined);
    }

    /// <summary>
    /// Copies window.json from beside the EXE into LocalAppData when the preferred file is missing.
    /// </summary>
    private static void MigrateWindowStateIfNeeded()
    {
        if (File.Exists(WindowStatePath) || !File.Exists(PortableWindowStatePath))
        {
            return;
        }

        if (PathsEqual(WindowStatePath, PortableWindowStatePath))
        {
            return;
        }

        try
        {
            File.Copy(PortableWindowStatePath, WindowStatePath, overwrite: false);
            TryDeleteFile(PortableWindowStatePath);
        }
        catch
        {
            // Window state is non-critical; LocalAppData write on next close will create it.
        }
    }

    /// <summary>
    /// Falls back to / migrates an encrypted vault beside the EXE into LocalAppData.
    /// </summary>
    private static string? HandlePortableEncrypted(ref Config? config)
    {
        string portablePath = PortableAccountsPath;

        if (config?.Accounts is { Count: > 0 })
        {
            Config portable = ReadEncrypted(portablePath);
            List<Account> portableAccounts = portable.Accounts ?? new List<Account>();
            config.Accounts ??= new List<Account>();

            int merged = MergeAccounts(config.Accounts, portableAccounts);
            if (merged > 0)
            {
                Save(config);
            }

            string mergeNote = merged > 0
                ? $"Merged {merged} account(s) from the EXE-folder vault into LocalAppData.\n\n"
                : "EXE-folder vault contained no new accounts (already in LocalAppData).\n\n";

            TryDeleteFile(PortableAccountsBackupPath);
            return TryDeleteLegacy(portablePath,
                successMessage: $"Found encrypted accounts.dat beside the EXE.\n\n{mergeNote}" +
                                $"Preferred store is:\n{AccountsPath}\n\n" +
                                $"The EXE-folder file was removed:\n{portablePath}",
                failureMessage: $"Found encrypted accounts.dat beside the EXE.\n\n{mergeNote}" +
                                $"Preferred store is:\n{AccountsPath}\n\n" +
                                $"Could not delete:\n{portablePath}\n\n" +
                                "Remove that file manually to avoid confusion.");
        }

        // No LocalAppData vault (or empty): fall back to EXE folder, then migrate.
        config = ReadEncrypted(portablePath);
        config.Accounts ??= new List<Account>();
        Save(config);

        TryDeleteFile(PortableAccountsBackupPath);
        return TryDeleteLegacy(portablePath,
            successMessage: $"Migrated encrypted vault from beside the EXE into:\n{AccountsPath}\n\n" +
                            $"The EXE-folder file was removed:\n{portablePath}",
            failureMessage: $"Migrated encrypted vault into:\n{AccountsPath}\n\n" +
                            $"but could not delete:\n{portablePath}\n\n" +
                            "Remove that file manually to avoid confusion.");
    }

    /// <summary>
    /// Migrates or merges leftover plaintext secrets; never silently ignores them.
    /// </summary>
    private static string HandleLegacyPlaintext(ref Config? config)
    {
        string legacyPath = LegacyAccountsPath;
        string legacyJson = File.ReadAllText(legacyPath);
        Config? legacy = JsonSerializer.Deserialize<Config>(legacyJson, JsonOptions);
        List<Account> legacyAccounts = legacy?.Accounts ?? new List<Account>();

        bool hasEncrypted = config?.Accounts is { Count: > 0 };

        if (!hasEncrypted)
        {
            config = legacy ?? new Config { Accounts = new List<Account>() };
            if (config.Accounts is null)
            {
                config.Accounts = new List<Account>();
            }

            Save(config);
            return TryDeleteLegacy(legacyPath,
                successMessage: $"Migrated plaintext accounts from:\n{legacyPath}\n\n" +
                                $"They are now stored encrypted at:\n{AccountsPath}\n\n" +
                                "The plaintext file was removed.",
                failureMessage: $"Migrated plaintext accounts into the encrypted store, but could not delete:\n{legacyPath}\n\n" +
                                "Remove that file manually — it still contains secrets.");
        }

        config!.Accounts ??= new List<Account>();
        int merged = MergeAccounts(config.Accounts, legacyAccounts);
        if (merged > 0)
        {
            Save(config);
        }

        string mergeNote = merged > 0
            ? $"Merged {merged} account(s) from leftover plaintext into the encrypted vault.\n\n"
            : "Leftover plaintext contained no new accounts (already in the vault).\n\n";

        return TryDeleteLegacy(legacyPath,
            successMessage: $"Found leftover plaintext accounts.json beside the EXE.\n\n{mergeNote}" +
                            $"The file was removed:\n{legacyPath}",
            failureMessage: $"Found leftover plaintext accounts.json beside the EXE.\n\n{mergeNote}" +
                            $"Could not delete:\n{legacyPath}\n\n" +
                            "Remove that file manually — it still contains secrets.");
    }

    private static int MergeAccounts(List<Account> target, List<Account> incoming)
    {
        int merged = 0;
        foreach (Account account in incoming)
        {
            if (string.IsNullOrWhiteSpace(account.Secret))
            {
                continue;
            }

            string key = NormalizeSecretKey(account.Secret);
            bool exists = target.Any(a =>
                string.Equals(NormalizeSecretKey(a.Secret), key, StringComparison.Ordinal));
            if (!exists)
            {
                target.Add(account);
                merged++;
            }
        }

        return merged;
    }

    private static Config ReadEncrypted(string path)
    {
        byte[] protectedBytes = File.ReadAllBytes(path);
        byte[] plain = ProtectedData.Unprotect(
            protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        string json = Encoding.UTF8.GetString(plain);
        return JsonSerializer.Deserialize<Config>(json, JsonOptions)
               ?? new Config { Accounts = new List<Account>() };
    }

    private static string TryDeleteLegacy(string legacyPath, string successMessage, string failureMessage)
    {
        try
        {
            File.Delete(legacyPath);
            return successMessage;
        }
        catch
        {
            return failureMessage;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSecretKey(string secret) =>
        secret.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();

    /// <summary>
    /// Writes the encrypted vault to LocalAppData, rotating the previous file to accounts.dat.bak first.
    /// </summary>
    public static void Save(Config config)
    {
        EnsureDataDirectory();
        string json = JsonSerializer.Serialize(config, JsonOptions);
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] protectedBytes = ProtectedData.Protect(
            plain, optionalEntropy: null, DataProtectionScope.CurrentUser);

        if (File.Exists(AccountsPath))
        {
            File.Copy(AccountsPath, AccountsBackupPath, overwrite: true);
        }

        AtomicFile.WriteAllBytes(AccountsPath, protectedBytes);
    }
}

/// <summary>Result of loading the account vault (and any leftover-plaintext handling).</summary>
internal sealed class LoadResult
{
    public LoadResult(Config? config, string? legacyMessage)
    {
        Config = config;
        LegacyMessage = legacyMessage;
    }

    public Config? Config { get; }
    public string? LegacyMessage { get; }
}

/// <summary>Write-temp-then-replace helpers to avoid truncated files on crash.</summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteAllText(string path, string contents)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
