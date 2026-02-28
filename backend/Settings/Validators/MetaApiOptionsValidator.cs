using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class MetaApiOptionsValidator : IValidateOptions<MetaApiOptions>
{
    public ValidateOptionsResult Validate(string? name, MetaApiOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.GraphApiBaseUrl))
            failures.Add($"{nameof(options.GraphApiBaseUrl)} is required.");
        else if (!Uri.TryCreate(options.GraphApiBaseUrl, UriKind.Absolute, out _))
            failures.Add($"{nameof(options.GraphApiBaseUrl)} must be an absolute URI.");

        if (string.IsNullOrWhiteSpace(options.OAuthDialogBaseUrl))
            failures.Add($"{nameof(options.OAuthDialogBaseUrl)} is required.");
        else if (!Uri.TryCreate(options.OAuthDialogBaseUrl, UriKind.Absolute, out _))
            failures.Add($"{nameof(options.OAuthDialogBaseUrl)} must be an absolute URI.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
