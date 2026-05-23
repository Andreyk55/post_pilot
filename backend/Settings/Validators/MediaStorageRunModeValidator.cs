using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

/// <summary>
/// Cross-validates MediaStorage against AppOptions.RunMode.
///
/// Server mode hands presigned upload URLs to a remote browser; the bytes must
/// land in object storage that survives container replacement. local-disk
/// writes to the API container's ephemeral filesystem and also routes uploads
/// through the legacy PUT /api/media/upload/{file} endpoint, which is gated to
/// Local mode and returns 404 in Server mode. Combining the two silently breaks
/// image uploads in production, so reject it at startup.
/// </summary>
public class MediaStorageRunModeValidator : IValidateOptions<MediaStorageOptions>
{
    private readonly IConfiguration _configuration;

    public MediaStorageRunModeValidator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, MediaStorageOptions options)
    {
        var runMode = _configuration["App:RunMode"] ?? string.Empty;
        var isServer = runMode.Equals("server", StringComparison.OrdinalIgnoreCase);

        if (isServer && options.IsLocalDisk)
        {
            return ValidateOptionsResult.Fail(
                "MediaStorage:Provider='local-disk' is not supported when App:RunMode='server'. " +
                "Server mode requires S3-compatible object storage (set MediaStorage__Provider=s3-compatible " +
                "and configure Bucket / InternalEndpoint / PublicUploadEndpoint / AccessKey / SecretKey). " +
                "Local-disk storage in Server mode would silently break image uploads — the API hands the " +
                "browser /api/media/upload/{file}, which 404s outside Local mode.");
        }

        return ValidateOptionsResult.Success;
    }
}
