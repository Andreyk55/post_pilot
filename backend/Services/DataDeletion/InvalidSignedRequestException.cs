namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Thrown by <see cref="IMetaSignedRequestVerifier"/> when a signed_request is
/// missing, malformed, uses an unexpected algorithm, or fails HMAC verification.
/// The controller maps this to a 400/401 and deletes nothing — an unverified
/// payload is never trusted.
///
/// The message is intentionally generic ("invalid signed_request") so verification
/// failures never reveal which check failed or leak any secret material.
/// </summary>
public sealed class InvalidSignedRequestException : Exception
{
    public InvalidSignedRequestException(string message) : base(message)
    {
    }
}
