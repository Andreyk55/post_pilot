using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using PostPilot.Api;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// L3: environment-scoped CORS (localhost only in Development) and ForwardedHeaders that trust a
/// known proxy set (never any peer).
/// </summary>
public class StartupHardeningTests
{
    private static readonly string[] ProdOrigins = { "https://www.publishharbor.com" };

    // ── CORS origin policy ────────────────────────────────────────────────────────

    [Fact]
    public void Localhost_is_allowed_in_development()
    {
        Assert.True(Startup.IsOriginAllowed("http://localhost:5173", isDevelopment: true, ProdOrigins));
    }

    [Fact]
    public void Localhost_is_rejected_outside_development()
    {
        Assert.False(Startup.IsOriginAllowed("http://localhost:5173", isDevelopment: false, ProdOrigins));
    }

    [Fact]
    public void Configured_production_origin_is_allowed_outside_development()
    {
        Assert.True(Startup.IsOriginAllowed("https://www.publishharbor.com", isDevelopment: false, ProdOrigins));
    }

    [Fact]
    public void Unknown_origin_is_rejected_outside_development()
    {
        Assert.False(Startup.IsOriginAllowed("https://evil.example.com", isDevelopment: false, ProdOrigins));
        Assert.False(Startup.IsOriginAllowed("http://localhost:5173", isDevelopment: false, System.Array.Empty<string>()));
    }

    // ── ForwardedHeaders known-proxy policy ───────────────────────────────────────

    [Fact]
    public void ForwardedHeaders_defaults_to_nonempty_known_networks()
    {
        var options = new ForwardedHeadersOptions();
        Startup.ConfigureForwardedHeaders(options, EmptyConfig());

        // Never "trust any peer": the trusted set must be non-empty (loopback + RFC1918 default).
        Assert.True(options.KnownProxies.Count + options.KnownIPNetworks.Count > 0);
        Assert.NotEmpty(options.KnownIPNetworks);
    }

    [Fact]
    public void ForwardedHeaders_parses_configured_known_proxies_and_networks()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownProxies:0"] = "10.1.2.3",
                ["ForwardedHeaders:KnownNetworks:0"] = "172.18.0.0/16",
            })
            .Build();

        var options = new ForwardedHeadersOptions();
        Startup.ConfigureForwardedHeaders(options, config);

        Assert.Contains(options.KnownProxies, p => p.ToString() == "10.1.2.3");
        Assert.Contains(options.KnownIPNetworks, n => n.BaseAddress.ToString() == "172.18.0.0" && n.PrefixLength == 16);
        // Because explicit config was supplied, the private-range default is NOT added.
        Assert.Single(options.KnownIPNetworks);
    }

    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
}
