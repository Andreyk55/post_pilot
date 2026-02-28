using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class PlatformSelectionOptionsValidator : IValidateOptions<PlatformSelectionOptions>
{
    public ValidateOptionsResult Validate(string? name, PlatformSelectionOptions options)
    {
        if (options.MaxPlatformsPerPost <= 0)
            return ValidateOptionsResult.Fail($"{nameof(options.MaxPlatformsPerPost)} must be > 0.");

        return ValidateOptionsResult.Success;
    }
}
