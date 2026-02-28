using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class PublishingOptionsValidator : IValidateOptions<PublishingOptions>
{
    public ValidateOptionsResult Validate(string? name, PublishingOptions options)
    {
        var failures = new List<string>();

        if (options.WorkerPollIntervalSeconds <= 0)
            failures.Add($"{nameof(options.WorkerPollIntervalSeconds)} must be > 0.");

        if (options.StuckPostThresholdMinutes <= 0)
            failures.Add($"{nameof(options.StuckPostThresholdMinutes)} must be > 0.");

        if (options.StuckPostRetryDelaySeconds <= 0)
            failures.Add($"{nameof(options.StuckPostRetryDelaySeconds)} must be > 0.");

        if (options.MediaDownloadUrlExpirationMinutes <= 0)
            failures.Add($"{nameof(options.MediaDownloadUrlExpirationMinutes)} must be > 0.");

        if (options.VideoDownloadUrlExpirationMinutes <= 0)
            failures.Add($"{nameof(options.VideoDownloadUrlExpirationMinutes)} must be > 0.");

        if (options.ImagePollMaxAttempts <= 0)
            failures.Add($"{nameof(options.ImagePollMaxAttempts)} must be > 0.");

        if (options.ImagePollIntervalSeconds <= 0)
            failures.Add($"{nameof(options.ImagePollIntervalSeconds)} must be > 0.");

        if (options.OAuthStateExpirationMinutes <= 0)
            failures.Add($"{nameof(options.OAuthStateExpirationMinutes)} must be > 0.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
