using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

public class MetaOptionsValidator : IValidateOptions<MetaOptions>
{
    public ValidateOptionsResult Validate(string? name, MetaOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.AppId))
            failures.Add("Meta:AppId is required. Set via Meta__AppId or META_APP_ID env var.");

        if (string.IsNullOrWhiteSpace(options.AppSecret))
            failures.Add("Meta:AppSecret is required. Set via Meta__AppSecret or META_APP_SECRET env var.");

        if (string.IsNullOrWhiteSpace(options.RedirectUri))
            failures.Add("Meta:RedirectUri is required. Set via Meta__RedirectUri env var or config.");
        else if (!Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out _))
            failures.Add("Meta:RedirectUri must be an absolute URI.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
