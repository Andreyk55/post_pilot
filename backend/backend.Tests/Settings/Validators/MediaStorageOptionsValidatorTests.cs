using PostPilot.Api.Settings;
using PostPilot.Api.Settings.Validators;
using Xunit;

namespace PostPilot.Api.Tests.Settings.Validators;

/// <summary>
/// Verifies the per-provider config validation matrix. The Supabase branch is
/// the new prod default; rejecting an incomplete Supabase config at startup is
/// the only thing that keeps a misconfigured deploy from silently 500-ing on
/// every upload.
/// </summary>
public class MediaStorageOptionsValidatorTests
{
    private static MediaStorageOptionsValidator Validator() => new();

    [Fact]
    public void Supabase_FullyConfigured_Succeeds()
    {
        var opts = new MediaStorageOptions
        {
            Provider = "supabase",
            Supabase = new SupabaseStorageOptions
            {
                Url = "https://abc.supabase.co",
                ServiceRoleKey = "eyJhbGciOi.fake.jwt",
                Bucket = "postpilot-media",
                SignedUrlExpirySeconds = 3600,
                MaxUploadBytes = 0,
            },
        };

        var result = Validator().Validate(null, opts);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void Supabase_MissingServiceRoleKey_Fails()
    {
        // This is the failure mode that matters most: someone forgets to set
        // the service-role key. We want a loud startup-time error, not a
        // 401 from Supabase on the first user upload.
        var opts = new MediaStorageOptions
        {
            Provider = "supabase",
            Supabase = new SupabaseStorageOptions
            {
                Url = "https://abc.supabase.co",
                ServiceRoleKey = "",
                Bucket = "postpilot-media",
                SignedUrlExpirySeconds = 3600,
            },
        };

        var result = Validator().Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ServiceRoleKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Supabase_NonHttpsUrl_Fails()
    {
        var opts = new MediaStorageOptions
        {
            Provider = "supabase",
            Supabase = new SupabaseStorageOptions
            {
                Url = "http://abc.supabase.co",
                ServiceRoleKey = "k",
                Bucket = "postpilot-media",
                SignedUrlExpirySeconds = 3600,
            },
        };

        var result = Validator().Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("https", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Supabase_ZeroExpiry_Fails()
    {
        var opts = new MediaStorageOptions
        {
            Provider = "supabase",
            Supabase = new SupabaseStorageOptions
            {
                Url = "https://abc.supabase.co",
                ServiceRoleKey = "k",
                Bucket = "postpilot-media",
                SignedUrlExpirySeconds = 0,
            },
        };

        var result = Validator().Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("SignedUrlExpirySeconds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Supabase_DoesNotRequireMinIO_RootFields()
    {
        // The whole point of switching providers: when MediaStorage__Provider=supabase,
        // the legacy MinIO env vars (Bucket/InternalEndpoint/PublicUploadEndpoint/
        // AccessKey/SecretKey) MUST NOT be required. This regression test guards
        // that — if someone re-enables those checks on the root object, prod
        // breaks at startup the second the MinIO vars stop being shipped.
        var opts = new MediaStorageOptions
        {
            Provider = "supabase",
            // Note: NO root-level Bucket/AccessKey/SecretKey/InternalEndpoint/PublicUploadEndpoint.
            Supabase = new SupabaseStorageOptions
            {
                Url = "https://abc.supabase.co",
                ServiceRoleKey = "k",
                Bucket = "postpilot-media",
                SignedUrlExpirySeconds = 3600,
            },
        };

        var result = Validator().Validate(null, opts);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void UnknownProvider_Fails()
    {
        var result = Validator().Validate(null, new MediaStorageOptions { Provider = "bogus" });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void S3Compatible_Incomplete_Fails()
    {
        var result = Validator().Validate(null, new MediaStorageOptions
        {
            Provider = "s3-compatible",
            // Missing everything required for S3.
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Bucket"));
        Assert.Contains(result.Failures!, f => f.Contains("InternalEndpoint"));
    }

    [Fact]
    public void LocalDisk_DoesNotRequireBucket()
    {
        var result = Validator().Validate(null, new MediaStorageOptions { Provider = "local-disk" });
        Assert.True(result.Succeeded);
    }
}
