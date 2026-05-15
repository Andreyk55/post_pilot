using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class MediaStorageOptionsValidator : IValidateOptions<MediaStorageOptions>
{
    private static readonly string[] ValidProviders = ["local-disk", "s3-compatible"];

    public ValidateOptionsResult Validate(string? name, MediaStorageOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Provider))
            failures.Add($"{nameof(options.Provider)} is required (e.g. 'local-disk' or 's3-compatible').");
        else if (!ValidProviders.Contains(options.Provider, StringComparer.OrdinalIgnoreCase))
            failures.Add($"{nameof(options.Provider)} must be one of: {string.Join(", ", ValidProviders)}. Got '{options.Provider}'.");

        if (options.PresignedUploadExpirationMinutes <= 0)
            failures.Add($"{nameof(options.PresignedUploadExpirationMinutes)} must be > 0.");

        // S3-compatible providers require full configuration.
        // local-disk skips these checks so dev without MinIO keeps working.
        if (options.IsS3Compatible)
        {
            if (string.IsNullOrWhiteSpace(options.Bucket))
                failures.Add($"{nameof(options.Bucket)} is required when Provider='s3-compatible'.");

            if (string.IsNullOrWhiteSpace(options.InternalEndpoint))
                failures.Add($"{nameof(options.InternalEndpoint)} is required when Provider='s3-compatible'.");
            else if (!Uri.TryCreate(options.InternalEndpoint, UriKind.Absolute, out _))
                failures.Add($"{nameof(options.InternalEndpoint)} must be an absolute URI.");

            if (string.IsNullOrWhiteSpace(options.PublicUploadEndpoint))
                failures.Add($"{nameof(options.PublicUploadEndpoint)} is required when Provider='s3-compatible'.");
            else if (!Uri.TryCreate(options.PublicUploadEndpoint, UriKind.Absolute, out _))
                failures.Add($"{nameof(options.PublicUploadEndpoint)} must be an absolute URI.");

            if (string.IsNullOrWhiteSpace(options.AccessKey))
                failures.Add($"{nameof(options.AccessKey)} is required when Provider='s3-compatible'.");

            if (string.IsNullOrWhiteSpace(options.SecretKey))
                failures.Add($"{nameof(options.SecretKey)} is required when Provider='s3-compatible'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
