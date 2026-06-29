using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class MediaUploadQuotaOptionsValidator : IValidateOptions<MediaUploadQuotaOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaUploadQuotaOptions options)
    {
        var failures = new List<string>();

        if (options.MaxUploadsPerUserPerWindow <= 0)
            failures.Add($"{nameof(options.MaxUploadsPerUserPerWindow)} must be > 0.");

        if (options.WindowHours <= 0)
            failures.Add($"{nameof(options.WindowHours)} must be > 0.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
