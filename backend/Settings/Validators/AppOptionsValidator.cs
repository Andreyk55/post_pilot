using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class AppOptionsValidator : IValidateOptions<AppOptions>
{
    private static readonly string[] ValidRunModes = ["local", "server"];

    public ValidateOptionsResult Validate(string? name, AppOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.RunMode))
            failures.Add("App:RunMode is required. Set via App__RunMode env var.");
        else if (!ValidRunModes.Contains(options.RunMode, StringComparer.OrdinalIgnoreCase))
            failures.Add($"App:RunMode must be 'local' or 'server', got '{options.RunMode}'.");

        if (!string.IsNullOrWhiteSpace(options.PublicUrl) &&
            !Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out _))
            failures.Add("App:PublicUrl must be an absolute URI when set.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
