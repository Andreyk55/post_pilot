using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

/// <summary>
/// Cross-validates MediaStorage against AppOptions.RunMode.
///
/// Server mode hands presigned upload URLs to a remote browser; the bytes must
/// land in object storage that survives container replacement. local-disk writes
/// to the API container's ephemeral filesystem and cannot produce a browser upload
/// URL at all — its CreateUploadUrlAsync throws NotSupportedException since the
/// legacy PUT /api/media/upload/{file} route was removed (media privacy redesign).
/// Combining local-disk with Server mode would break image uploads, so reject it
/// at startup.
///
/// Supabase + S3-compatible both satisfy the "real object storage" requirement.
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
                "Server mode requires object storage. Set MediaStorage__Provider=supabase " +
                "(recommended) or MediaStorage__Provider=s3-compatible. Local-disk storage " +
                "cannot mint browser upload URLs — its CreateUploadUrlAsync throws because the " +
                "legacy /api/media/upload route was removed — so uploads would break entirely.");
        }

        return ValidateOptionsResult.Success;
    }
}
