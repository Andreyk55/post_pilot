using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Default <see cref="IMetaSignedRequestVerifier"/>. Implements Meta's documented
/// signed_request scheme:
///
///   signed_request = base64url(signature) + "." + base64url(payloadJson)
///   signature      = HMAC-SHA256(payloadJson_base64url_string, AppSecret)
///
/// We re-compute the HMAC over the *encoded* payload string (the bytes exactly as
/// transmitted, before base64url-decoding) and compare it to the provided signature
/// in constant time. Only after that match do we decode and read the payload.
/// </summary>
public sealed class MetaSignedRequestVerifier : IMetaSignedRequestVerifier
{
    private const string ExpectedAlgorithm = "HMAC-SHA256";

    private readonly MetaOptions _metaOptions;

    public MetaSignedRequestVerifier(MetaOptions metaOptions)
    {
        _metaOptions = metaOptions;
    }

    public MetaSignedRequestPayload Verify(string signedRequest)
    {
        if (string.IsNullOrWhiteSpace(signedRequest))
            throw new InvalidSignedRequestException("Missing signed_request.");

        var appSecret = _metaOptions.AppSecret;
        if (string.IsNullOrEmpty(appSecret))
        {
            // Misconfiguration, not a caller error. Fail closed rather than accept
            // anything — but keep the message generic so we never hint at secrets.
            throw new InvalidSignedRequestException("Cannot verify signed_request.");
        }

        var parts = signedRequest.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new InvalidSignedRequestException("Malformed signed_request.");

        var encodedSignature = parts[0];
        var encodedPayload = parts[1];

        byte[] providedSignature;
        byte[] payloadBytes;
        try
        {
            providedSignature = Base64UrlDecode(encodedSignature);
            payloadBytes = Base64UrlDecode(encodedPayload);
        }
        catch (FormatException)
        {
            throw new InvalidSignedRequestException("Malformed signed_request encoding.");
        }

        // HMAC is computed over the ENCODED payload string exactly as received.
        var expectedSignature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(appSecret),
            Encoding.UTF8.GetBytes(encodedPayload));

        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            throw new InvalidSignedRequestException("Invalid signed_request signature.");

        Dictionary<string, object>? payload;
        try
        {
            payload = ParsePayload(payloadBytes);
        }
        catch (JsonException)
        {
            throw new InvalidSignedRequestException("Malformed signed_request payload.");
        }

        if (payload is null)
            throw new InvalidSignedRequestException("Empty signed_request payload.");

        if (payload.TryGetValue("algorithm", out var algo)
            && algo is string algoStr
            && !string.Equals(algoStr, ExpectedAlgorithm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidSignedRequestException("Unsupported signed_request algorithm.");
        }

        if (!payload.TryGetValue("user_id", out var userIdObj)
            || userIdObj is not string userId
            || string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidSignedRequestException("signed_request missing user_id.");
        }

        return new MetaSignedRequestPayload(userId, payload);
    }

    /// <summary>
    /// Flattens the payload JSON object into a string-keyed dictionary. Scalars are
    /// stored as string/long/double/bool; nested values keep their JSON text. We only
    /// need <c>user_id</c> and <c>algorithm</c>, so a shallow projection is enough.
    /// </summary>
    private static Dictionary<string, object>? ParsePayload(byte[] payloadBytes)
    {
        using var doc = JsonDocument.Parse(payloadBytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.GetRawText(),
            };
        }
        return result;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var sb = new StringBuilder(input.Length + 3);
        sb.Append(input.Replace('-', '+').Replace('_', '/'));
        switch (sb.Length % 4)
        {
            case 2: sb.Append("=="); break;
            case 3: sb.Append('='); break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(sb.ToString());
    }
}
