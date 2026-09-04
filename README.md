# WinOTP

A lightweight Windows desktop app that generates **TOTP** one-time passwords (the same codes used by Google Authenticator, Microsoft Authenticator, and similar apps). Built with **.NET 8** and **WPF**.

## Features

- Live TOTP codes with a per-account countdown
- One-click **Copy** (clipboard auto-clears after ~30 seconds, and on window close)
- **Import QR** from a PNG/JPG (`otpauth://totp/...`)
- **Delete** accounts (button or right-click menu)
- Duplicate-secret warning on import
- Window size/position remembered between sessions
- Secrets stored encrypted under your Windows user profile (DPAPI), with `accounts.dat.bak` rotation before overwrite; falls back to / migrates from a vault beside the EXE

## Requirements

- **Windows 10/11 64-bit**
- **To build from source:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **To run the standalone zip:** [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)  
- **To run the self-contained zip:** no separate .NET install

## Quick start

Download a release zip from [Releases](https://github.com/xuanthanhvc/windows-otp-generator/releases), unzip, and run `WinOtp.exe`.

| Package | Best for |
|---------|----------|
| `*-standalone.zip` | Small download; machine already has (or can install) .NET 8 Desktop Runtime |
| `*-self-contained.zip` | Any PC with no .NET install |

From source:

```bash
git clone https://github.com/xuanthanhvc/windows-otp-generator.git
cd windows-otp-generator
dotnet run -c Release
```

Publish both packages:

```bash
# Standalone (framework-dependent — needs .NET 8 Desktop Runtime)
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish\standalone

# Self-contained (no .NET install required)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\self-contained
```

## Adding accounts

### Option A — Import a QR code

1. Export or screenshot the authenticator QR from your service (or an existing authenticator app).
2. In WinOTP, click **Import QR** and select the image.
3. The account is saved automatically.

### Option B — Seed file (migration / advanced)

**Preferred store** (always used for new saves):

`%LocalAppData%\WinOtp\accounts.dat`

**Also supported beside the EXE** (read / migrate only):

| File | Behavior |
|------|----------|
| `accounts.dat` | Encrypted portable vault — loaded if LocalAppData is empty, or merged if both exist; then migrated into LocalAppData |
| `accounts.json` | Legacy plaintext — migrated/merged into LocalAppData and removed when possible |
| `window.json` | Copied into LocalAppData when the preferred window state file is missing |

After migration, WinOTP keeps using LocalAppData so moving or deleting the EXE folder does not lose your vault.

For reference only, see [`accounts.json.example`](accounts.json.example):

```json
{
  "accounts": [
    {
      "name": "Example (user@example.com)",
      "secret": "JBSWY3DPEHPK3PXP",
      "digits": 6,
      "period": 30,
      "algorithm": "SHA1"
    }
  ]
}
```

| Field | Default | Notes |
|-------|---------|--------|
| `name` | required | Display label |
| `secret` | required | Base32 secret (spaces/hyphens/`=` allowed) |
| `digits` | `6` | Must be 6–8 (unsupported values are rejected on import / shown as invalid on load) |
| `period` | `30` | Seconds per code |
| `algorithm` | `SHA1` | `SHA1`, `SHA256`, `SHA512` (hyphenated forms like `SHA-256` accepted) |

**Never commit real secrets.** `accounts.json` / `accounts.dat` are gitignored.

## Security notes

- Account seeds are protected with **Windows DPAPI** (`CurrentUser`). Only the same Windows user on the same machine can decrypt them.
- Copied codes are cleared from the clipboard after about 30 seconds if the clipboard still holds that code, and again when the window closes.
- A failed vault load disables Import/Delete/Save so a corrupt read cannot overwrite `accounts.dat`. A `.bak` of the previous encrypted file is kept under LocalAppData.
- DPAPI still binds the vault to the same Windows user/machine even when files sit beside the EXE.
- This is a personal utility, not a full password manager. Treat the machine and user account as the security boundary.

## Project layout

| File | Role |
|------|------|
| `Totp.cs` | Base32 + HOTP/TOTP (RFC 4226 / 6238) |
| `AccountStore.cs` | LocalAppData + EXE-folder paths, DPAPI load/save, migration |
| `MainWindow.xaml(.cs)` | UI, QR import, clipboard, list refresh |
| `accounts.json.example` | Safe sample config (no real secrets) |

## Development

```bash
dotnet build
dotnet run
```

Stack: C# / WPF, [ZXing.Net](https://www.nuget.org/packages/ZXing.Net) for QR decoding, `System.Security.Cryptography.ProtectedData` for at-rest encryption.

## License

Use and modify for personal or internal needs. Add an explicit license file if you publish the project publicly.
