using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// The media object that should actually be validated/published for a given target.
/// </summary>
/// <param name="StorageKey">Storage key whose bytes should be decoded for validation.</param>
/// <param name="MimeType">Authoritative MIME type for that object (from the Media row).</param>
/// <param name="SizeBytes">Authoritative size if known (else the caller falls back to the file length).</param>
/// <param name="IsDerivative">True when this is the Instagram JPEG derivative, not the original upload.</param>
/// <param name="DerivativeMissing">
/// True when Instagram needs the JPEG derivative of a PNG but none exists (generation failed or
/// never ran). The caller must BLOCK with <see cref="DTOs.MediaValidationErrorCodes.InstagramDerivativeMissing"/>.
/// </param>
public record EffectiveMedia(
    string StorageKey,
    string MimeType,
    long? SizeBytes,
    bool IsDerivative,
    bool DerivativeMissing);

/// <summary>
/// Single source of truth for "which bytes do we validate/publish for this target?".
///
/// <para>
/// Facebook (and everything that is not an Instagram PNG image) validates the original upload.
/// Instagram requires JPEG, so a PNG image is validated against its server-generated JPEG
/// derivative; when that derivative is missing the result signals a hard block. Videos never
/// have a derivative — they always resolve to the original.
/// </para>
///
/// <para>Pure and side-effect free so it is trivially unit-testable and shared by both the
/// authoritative <see cref="MediaValidationGate"/> and the advisory /api/media/validate path.</para>
/// </summary>
public static class EffectiveMediaResolver
{
    public static EffectiveMedia Resolve(Entities.Media media, MediaType mediaType, Platform platform)
    {
        // Only Instagram images can need the PNG → JPEG derivative.
        var isInstagramPngImage =
            mediaType == MediaType.Image
            && platform == Platform.Instagram
            && string.Equals(media.ContentType, "image/png", StringComparison.OrdinalIgnoreCase);

        if (!isInstagramPngImage)
        {
            return new EffectiveMedia(
                StorageKey: media.StorageKey,
                MimeType: media.ContentType,
                SizeBytes: media.SizeBytes,
                IsDerivative: false,
                DerivativeMissing: false);
        }

        if (string.IsNullOrEmpty(media.InstagramImageStorageKey))
        {
            // PNG selected for Instagram but no JPEG derivative was generated → block.
            return new EffectiveMedia(
                StorageKey: media.StorageKey,
                MimeType: media.ContentType,
                SizeBytes: media.SizeBytes,
                IsDerivative: false,
                DerivativeMissing: true);
        }

        return new EffectiveMedia(
            StorageKey: media.InstagramImageStorageKey,
            MimeType: media.InstagramImageMimeType ?? "image/jpeg",
            SizeBytes: media.InstagramImageSizeBytes,
            IsDerivative: true,
            DerivativeMissing: false);
    }
}
