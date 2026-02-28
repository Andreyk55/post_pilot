using Microsoft.Extensions.Options;
using PostPilot.Api.Services.Ai;

namespace PostPilot.Api.Settings.Validators;

public class GeminiSettingsValidator : IValidateOptions<GeminiSettings>
{
    public ValidateOptionsResult Validate(string? name, GeminiSettings options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            failures.Add("GEMINI_API_KEY environment variable is required.");

        if (string.IsNullOrWhiteSpace(options.Model))
            failures.Add("GEMINI_MODEL environment variable is required.");

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            failures.Add($"{nameof(options.BaseUrl)} is required.");
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
            failures.Add($"{nameof(options.BaseUrl)} must be an absolute URI.");

        if (options.TimeoutSeconds <= 0)
            failures.Add($"{nameof(options.TimeoutSeconds)} must be > 0.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
