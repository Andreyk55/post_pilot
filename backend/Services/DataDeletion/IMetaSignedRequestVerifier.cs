namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Verifies and decodes a Meta <c>signed_request</c> (the payload Facebook POSTs to
/// the Data Deletion Callback). Verification is HMAC-SHA256 over the encoded payload
/// using the Meta App Secret — the ONLY thing that makes <see cref="MetaSignedRequestPayload.UserId"/>
/// trustworthy. Nothing in the payload is trusted before the signature checks out.
/// </summary>
public interface IMetaSignedRequestVerifier
{
    /// <summary>
    /// Parses, verifies, and returns the decoded payload.
    /// </summary>
    /// <exception cref="InvalidSignedRequestException">
    /// Missing/empty, malformed (wrong number of parts, bad base64url), wrong
    /// algorithm, missing user_id, or signature mismatch.
    /// </exception>
    MetaSignedRequestPayload Verify(string signedRequest);
}

/// <summary>
/// Decoded, verified Meta signed_request payload.
/// </summary>
/// <param name="UserId">The Meta app-scoped user id (matches MetaConnection.ProviderAccountId).</param>
/// <param name="RawPayload">The full decoded JSON payload, for diagnostics. May be null.</param>
public sealed record MetaSignedRequestPayload(
    string UserId,
    IReadOnlyDictionary<string, object>? RawPayload = null);
