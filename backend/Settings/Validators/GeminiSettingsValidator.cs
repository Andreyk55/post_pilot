using Microsoft.Extensions.Options;
using PostPilot.Api.Services.Ai;

namespace PostPilot.Api.Settings.Validators;

public class GeminiSettingsValidator : IValidateOptions<GeminiSettings>
{
    public ValidateOptionsResult Validate(string? name, GeminiSettings options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            failures.Add("Gemini:ApiKey is required. Set via Gemini__ApiKey env var.");

        if (string.IsNullOrWhiteSpace(options.Model))
            failures.Add("Gemini:Model is required. Set via Gemini__Model env var.");

        if (string.IsNullOrWhiteSpace(options.VisionModel))
        {
            failures.Add("Gemini:VisionModel is required. Set via Gemini__VisionModel env var.");
        }
        else if (options.VisionModel.StartsWith("gemma", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Gemini:VisionModel must be a vision-capable Gemini model, not a Gemma text model.");
        }

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
