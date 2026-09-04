using System.Security.Cryptography;

namespace WinOtp;

/// <summary>
/// Pure TOTP logic: Base32 decoding (RFC 4648), HOTP (RFC 4226) and TOTP (RFC 6238).
/// All methods are static and side-effect free.
/// </summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public const int MinDigits = 6;
    public const int MaxDigits = 8;

    /// <summary>
    /// Normalizes algorithm names (SHA1, SHA-256, sha256, …).
    /// Null/empty defaults to SHA1; unknown values throw.
    /// </summary>
    public static string NormalizeAlgorithm(string? algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            return "SHA1";
        }

        string normalized = algorithm.Trim().ToUpperInvariant()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);

        return normalized switch
        {
            "SHA1" => "SHA1",
            "SHA256" => "SHA256",
            "SHA512" => "SHA512",
            _ => throw new ArgumentException(
                $"Unsupported algorithm '{algorithm}'. Use SHA1, SHA256, or SHA512.")
        };
    }

    /// <summary>True when <paramref name="digits"/> is in the supported 6–8 range.</summary>
    public static bool IsSupportedDigits(int digits) =>
        digits >= MinDigits && digits <= MaxDigits;

    /// <summary>Clamps TOTP digit length to the supported range.</summary>
    public static int ClampDigits(int digits) =>
        Math.Clamp(digits, MinDigits, MaxDigits);

    /// <summary>
    /// Computes the current TOTP code and how many seconds remain in the current step.
    /// </summary>
    public static (string Code, int SecondsRemaining) Generate(
        string secret,
        int digits = 6,
        int period = 30,
        string algorithm = "SHA1")
    {
        digits = ClampDigits(digits);
        algorithm = NormalizeAlgorithm(algorithm);

        if (period <= 0)
        {
            period = 30;
        }

        long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long counter = unixTime / period;
        int secondsRemaining = (int)(period - (unixTime % period));

        byte[] key = Base32Decode(secret);
        string code = ComputeHotp(key, counter, digits, algorithm);
        return (code, secondsRemaining);
    }

    /// <summary>
    /// HOTP per RFC 4226 using the given hashing algorithm.
    /// </summary>
    private static string ComputeHotp(byte[] key, long counter, int digits, string algorithm)
    {
        // 8-byte big-endian counter.
        byte[] counterBytes = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        byte[] hash = ComputeHmac(algorithm, key, counterBytes);

        // Dynamic truncation.
        int offset = hash[^1] & 0x0F;
        int binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        int otp = binaryCode % Pow10(digits);
        return otp.ToString().PadLeft(digits, '0');
    }

    private static int Pow10(int digits)
    {
        int result = 1;
        for (int i = 0; i < digits; i++)
        {
            result *= 10;
        }

        return result;
    }

    private static byte[] ComputeHmac(string algorithm, byte[] key, byte[] message)
    {
        using HMAC hmac = algorithm switch
        {
            "SHA256" => new HMACSHA256(key),
            "SHA512" => new HMACSHA512(key),
            "SHA1" => new HMACSHA1(key),
            _ => throw new ArgumentException(
                $"Unsupported algorithm '{algorithm}'. Use SHA1, SHA256, or SHA512.")
        };
        return hmac.ComputeHash(message);
    }

    /// <summary>
    /// Decodes a Base32 (RFC 4648) secret into raw bytes. Ignores spaces and padding,
    /// and is case-insensitive.
    /// </summary>
    public static byte[] Base32Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new FormatException("Secret is empty.");
        }

        // Normalize: uppercase, drop spaces and padding.
        string clean = input.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();

        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char c in clean)
        {
            int value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException($"Invalid Base32 character '{c}'.");
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        if (bytes.Count == 0)
        {
            throw new FormatException("Secret did not decode to any bytes.");
        }

        return bytes.ToArray();
    }
}
