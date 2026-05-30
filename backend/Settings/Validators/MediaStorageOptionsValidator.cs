using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class MediaStorageOptionsValidator : IValidateOptions<MediaStorageOptions>
{
    private static readonly string[] ValidProviders = ["local-disk", "s3-compatible", "supabase"];

    public ValidateOptionsResult Validate(string? name, MediaStorageOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Provider))
            failures.Add($"{nameof(options.Provider)} is required (one of: {string.Join(", ", ValidProviders)}).");
        else if (!ValidProviders.Contains(options.Provider, StringComparer.OrdinalIgnoreCase))
            failures.Add($"{nameof(options.Provider)} must be one of: {string.Join(", ", ValidProviders)}. Got '{options.Provider}'.");

        if (options.PresignedUploadExpirationMinutes <= 0)
            failures.Add($"{nameof(options.PresignedUploadExpirationMinutes)} must be > 0.");

        // S3-compatible providers require the root-level S3 config.
        // local-disk skips these checks so dev without MinIO keeps working.
        // Supabase ignores them entirely — its config lives under MediaStorage:Supabase.
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

        if (options.IsSupabase)
        {
            var s = options.Supabase;
            if (string.IsNullOrWhiteSpace(s.Url))
                failures.Add("MediaStorage:Supabase:Url is required when Provider='supabase'.");
            else if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var supaUri) || supaUri.Scheme != Uri.UriSchemeHttps)
                failures.Add("MediaStorage:Supabase:Url must be an absolute https:// URL (e.g. https://YOUR_PROJECT.supabase.co).");

            if (string.IsNullOrWhiteSpace(s.ServiceRoleKey))
                failures.Add("MediaStorage:Supabase:ServiceRoleKey is required when Provider='supabase'. " +
                             "Set MediaStorage__Supabase__ServiceRoleKey in the backend/worker env — never in any frontend build.");

            if (string.IsNullOrWhiteSpace(s.Bucket))
                failures.Add("MediaStorage:Supabase:Bucket is required when Provider='supabase'.");

            if (s.SignedUrlExpirySeconds <= 0)
                failures.Add("MediaStorage:Supabase:SignedUrlExpirySeconds must be > 0.");

            if (s.MaxUploadBytes < 0)
                failures.Add("MediaStorage:Supabase:MaxUploadBytes must be >= 0 (0 disables the cap).");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
