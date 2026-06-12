using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class AiRateLimiterOptionsValidator : IValidateOptions<AiRateLimiterOptions>
{
    public ValidateOptionsResult Validate(string? name, AiRateLimiterOptions options)
    {
        var failures = new List<string>();

        if (options.MaxCallsPerDay <= 0)
            failures.Add($"{nameof(options.MaxCallsPerDay)} must be > 0.");

        if (options.WindowHours <= 0)
            failures.Add($"{nameof(options.WindowHours)} must be > 0.");

        // OverrideMaxCallsPerDay is optional. When missing/invalid (<= 0) the limiter
        // falls back to MaxCallsPerDay, so we do NOT fail validation here — we only
        // reject a negative value to catch obvious misconfiguration. Zero is treated
        // as "not set" by the fallback logic and is allowed.
        if (options.OverrideMaxCallsPerDay < 0)
            failures.Add($"{nameof(options.OverrideMaxCallsPerDay)} must be >= 0.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
