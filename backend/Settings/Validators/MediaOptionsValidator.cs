using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class MediaOptionsValidator : IValidateOptions<MediaOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaOptions options)
    {
        var failures = new List<string>();

        if (options.UploadUrlExpirationMinutes <= 0)
            failures.Add($"{nameof(options.UploadUrlExpirationMinutes)} must be > 0.");

        if (options.MaxImageFileSizeBytes <= 0)
            failures.Add($"{nameof(options.MaxImageFileSizeBytes)} must be > 0.");

        if (options.MaxVideoFileSizeBytes <= 0)
            failures.Add($"{nameof(options.MaxVideoFileSizeBytes)} must be > 0.");

        if (string.IsNullOrWhiteSpace(options.LocalServerBaseUrl))
            failures.Add($"{nameof(options.LocalServerBaseUrl)} is required.");
        else if (!Uri.TryCreate(options.LocalServerBaseUrl, UriKind.Absolute, out _))
            failures.Add($"{nameof(options.LocalServerBaseUrl)} must be an absolute URI.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
