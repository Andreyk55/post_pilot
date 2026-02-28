using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class AiCacheOptionsValidator : IValidateOptions<AiCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, AiCacheOptions options)
    {
        var failures = new List<string>();

        if (options.CaptionAssistMinutes <= 0)
            failures.Add($"{nameof(options.CaptionAssistMinutes)} must be > 0.");

        if (options.LanguageDetectionMinutes <= 0)
            failures.Add($"{nameof(options.LanguageDetectionMinutes)} must be > 0.");

        if (options.GoogleAiClientMinutes <= 0)
            failures.Add($"{nameof(options.GoogleAiClientMinutes)} must be > 0.");

        if (options.PostTimeSuggestionMinutes <= 0)
            failures.Add($"{nameof(options.PostTimeSuggestionMinutes)} must be > 0.");

        if (options.AssetResolverDownloadUrlExpirationMinutes <= 0)
            failures.Add($"{nameof(options.AssetResolverDownloadUrlExpirationMinutes)} must be > 0.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
