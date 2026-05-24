using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.PrivateAccess;

/// <summary>
/// Issues and validates the opaque cookie value used by the private-access
/// gate. Format: "{expiresAtUnixSeconds}.{base64url(HMAC-SHA256)}". The HMAC
/// is keyed by PrivateAccessOptions.CookieSigningKey (falls back to a value
/// derived from PasswordHash) so cookies cannot be forged client-side and
/// rotating the password/key invalidates all outstanding cookies.
/// </summary>
public interface IPrivateAccessTokenService
{
    string IssueToken(DateTimeOffset expiresAt);
    bool ValidateToken(string token);
}

public class PrivateAccessTokenService : IPrivateAccessTokenService
{
    private readonly PrivateAccessOptions _options;

    public PrivateAccessTokenService(PrivateAccessOptions options)
    {
        _options = options;
    }

    public string IssueToken(DateTimeOffset expiresAt)
    {
        var expiresUnix = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = Sign(expiresUnix);
        return $"{expiresUnix}.{sig}";
    }

    public bool ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return false;

        var expiresUnixStr = token[..dot];
        var providedSig = token[(dot + 1)..];

        if (!long.TryParse(expiresUnixStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresUnix))
            return false;

        if (DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= DateTimeOffset.UtcNow)
            return false;

        var expectedSig = Sign(expiresUnixStr);

        // CryptographicOperations.FixedTimeEquals requires equal-length spans.
        var providedBytes = Encoding.UTF8.GetBytes(providedSig);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSig);
        if (providedBytes.Length != expectedBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private string Sign(string payload)
    {
        var keyMaterial = !string.IsNullOrEmpty(_options.CookieSigningKey)
            ? _options.CookieSigningKey
            : "postpilot-private-access::" + _options.PasswordHash;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keyMaterial));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
