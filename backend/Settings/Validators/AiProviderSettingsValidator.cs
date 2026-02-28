using Microsoft.Extensions.Options;
using PostPilot.Api.Services.Ai;

namespace PostPilot.Api.Settings.Validators;

public class AiProviderSettingsValidator : IValidateOptions<AiProviderSettings>
{
    public ValidateOptionsResult Validate(string? name, AiProviderSettings options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.LanguageDetectorProvider))
            failures.Add($"{nameof(options.LanguageDetectorProvider)} is required.");

        if (string.IsNullOrWhiteSpace(options.CaptionGeneratorProvider))
            failures.Add($"{nameof(options.CaptionGeneratorProvider)} is required.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
