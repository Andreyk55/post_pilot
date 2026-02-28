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

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
