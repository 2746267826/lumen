using System.Security.Cryptography;
using System.Text;

namespace Lumen.Server.Infrastructure;

public sealed class CryptoService
{
    private const int PasswordIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public readonly byte[] ShareCookieSecret;

    public CryptoService(LumenOptions options)
    {
        var path = Path.Combine(options.DataDir, options.ShareSecretFile);
        Directory.CreateDirectory(options.DataDir);
        if (File.Exists(path))
        {
            ShareCookieSecret = File.ReadAllBytes(path);
        }
        else
        {
            ShareCookieSecret = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, ShareCookieSecret);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        if (ShareCookieSecret.Length < 32)
        {
            throw new InvalidOperationException("Share secret must contain at least 32 bytes.");
        }
    }

    public string NewToken(int bytes = 32) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    public string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return $"pbkdf2-sha256${PasswordIterations}${Base64UrlEncode(salt)}${Base64UrlEncode(hash)}";
    }

    public bool VerifyPassword(string password, string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var parts = encoded.Split('$');
        if (parts.Length != 4 ||
            parts[0] != "pbkdf2-sha256" ||
            !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Base64UrlDecode(parts[2]);
            var expected = Base64UrlDecode(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public string CreateShareCookie(string token, string? passwordHash)
    {
        var payload = $"{token}|{passwordHash ?? "-"}";
        using var hmac = new HMACSHA256(ShareCookieSecret);
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    public bool VerifyShareCookie(string token, string? passwordHash, string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return false;
        }

        try
        {
            var actual = Base64UrlDecode(cookie);
            var expected = Base64UrlDecode(CreateShareCookie(token, passwordHash));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
