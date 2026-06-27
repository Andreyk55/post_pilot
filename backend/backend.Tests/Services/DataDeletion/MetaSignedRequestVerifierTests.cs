using System.Security.Cryptography;
using System.Text;
using PostPilot.Api.Services.DataDeletion;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.DataDeletion;

/// <summary>
/// Pins the signed_request verification contract: only correctly-HMAC-signed payloads
/// are accepted, and user_id is extracted faithfully. Everything else throws
/// <see cref="InvalidSignedRequestException"/> so the controller deletes nothing.
/// </summary>
public class MetaSignedRequestVerifierTests
{
    private const string AppSecret = "test-app-secret-value";

    private static MetaSignedRequestVerifier NewVerifier(string secret = AppSecret) =>
        new(new MetaOptions { AppId = "app", AppSecret = secret, RedirectUri = "https://x" });

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BuildSignedRequest(string payloadJson, string secret)
    {
        var encodedPayload = B64Url(Encoding.UTF8.GetBytes(payloadJson));
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(encodedPayload));
        return $"{B64Url(sig)}.{encodedPayload}";
    }

    [Fact]
    public void Valid_signed_request_is_accepted_and_user_id_extracted()
    {
        var signed = BuildSignedRequest(
            "{\"algorithm\":\"HMAC-SHA256\",\"user_id\":\"1234567890\",\"issued_at\":1700000000}", AppSecret);

        var payload = NewVerifier().Verify(signed);

        Assert.Equal("1234567890", payload.UserId);
        Assert.NotNull(payload.RawPayload);
        Assert.Equal("1234567890", payload.RawPayload!["user_id"]);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var signed = BuildSignedRequest(
            "{\"algorithm\":\"HMAC-SHA256\",\"user_id\":\"alice\"}", AppSecret);

        // Swap the payload for a different one while keeping the original signature.
        var parts = signed.Split('.');
        var forgedPayload = B64Url(Encoding.UTF8.GetBytes("{\"algorithm\":\"HMAC-SHA256\",\"user_id\":\"mallory\"}"));
        var tampered = $"{parts[0]}.{forgedPayload}";

        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier().Verify(tampered));
    }

    [Fact]
    public void Invalid_signature_is_rejected()
    {
        var signed = BuildSignedRequest("{\"user_id\":\"x\"}", AppSecret);
        var parts = signed.Split('.');
        var badSig = B64Url(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier().Verify($"{badSig}.{parts[1]}"));
    }

    [Fact]
    public void Wrong_app_secret_is_rejected()
    {
        // Signed with one secret, verified with another → signature mismatch.
        var signed = BuildSignedRequest("{\"user_id\":\"x\"}", "a-different-secret");

        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier(AppSecret).Verify(signed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Missing_signed_request_is_rejected(string? input)
    {
        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier().Verify(input!));
    }

    [Theory]
    [InlineData("only-one-part")]
    [InlineData("a.b.c")]
    [InlineData(".payload")]
    [InlineData("sig.")]
    public void Malformed_structure_is_rejected(string input)
    {
        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier().Verify(input));
    }

    [Fact]
    public void Malformed_base64_is_rejected()
    {
        // Valid structure (two parts) but the signature part is not base64url.
        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier().Verify("@@@@.@@@@"));
    }

    [Fact]
    public void Wrong_algorithm_is_rejected()
    {
        var signed = BuildSignedRequest(
            "{\"algorithm\":\"AES\",\"user_id\":\"x\"}", AppSecret);

        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier().Verify(signed));
    }

    [Fact]
    public void Missing_user_id_is_rejected()
    {
        var signed = BuildSignedRequest("{\"algorithm\":\"HMAC-SHA256\",\"issued_at\":1}", AppSecret);

        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier().Verify(signed));
    }

    [Fact]
    public void Unconfigured_app_secret_fails_closed()
    {
        var signed = BuildSignedRequest("{\"user_id\":\"x\"}", AppSecret);

        Assert.Throws<InvalidSignedRequestException>(() => NewVerifier(secret: "").Verify(signed));
    }
}
