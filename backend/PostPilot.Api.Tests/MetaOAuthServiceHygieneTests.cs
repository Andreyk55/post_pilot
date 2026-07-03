using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// M4/M5: log-redaction + token-out-of-URL helpers, and proof that the instagram/debug service
/// output never carries tokens, token prefixes, or raw Graph bodies.
/// </summary>
public class MetaOAuthServiceHygieneTests : IDisposable
{
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private const string UserToken = "USER_SECRET_TOKEN_abcdef123456";
    private const string PageToken = "PAGE_SECRET_TOKEN_zyxwvu987654";
    private const string PageId = "fb-page-1";

    private readonly AppDbContext _db;

    public MetaOAuthServiceHygieneTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ── M5: RedactSensitive ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"access_token\":\"EAAB123secret\",\"id\":\"1\"}", "EAAB123secret")]
    [InlineData("{\"refresh_token\":\"rt_secret_value\"}", "rt_secret_value")]
    [InlineData("access_token=EAAB999zzz&foo=bar", "EAAB999zzz")]
    [InlineData("{\"token\":\"tok_secret\"}", "tok_secret")]
    [InlineData("{\"client_secret\":\"csecret\"}", "csecret")]
    [InlineData("Authorization: Bearer abctokvalue", "abctokvalue")]
    public void RedactSensitive_removes_secret_values(string input, string secret)
    {
        var redacted = MetaOAuthService.RedactSensitive(input);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactSensitive_keeps_nonsensitive_fields()
    {
        var redacted = MetaOAuthService.RedactSensitive("{\"id\":\"123\",\"name\":\"Acme\",\"access_token\":\"secret\"}");

        Assert.Contains("\"id\":\"123\"", redacted);
        Assert.Contains("Acme", redacted);
        Assert.DoesNotContain("secret", redacted);
    }

    // ── M5: StripAccessTokenFromQuery ───────────────────────────────────────────

    [Fact]
    public void Strip_removes_auth_param_but_keeps_field_request()
    {
        // The bare `access_token` FIELD (no '=') must survive; the auth PARAM must be removed.
        var url = "https://graph.facebook.com/v21.0/me/accounts?fields=id,name,access_token&access_token=SECRET";

        var stripped = MetaOAuthService.StripAccessTokenFromQuery(url);

        Assert.DoesNotContain("SECRET", stripped);
        Assert.DoesNotContain("access_token=SECRET", stripped);
        Assert.Contains("fields=id,name,access_token", stripped);
    }

    [Theory]
    [InlineData("https://g/me?access_token=SECRET", "https://g/me")]
    [InlineData("https://g/me?fields=id&access_token=SECRET", "https://g/me?fields=id")]
    [InlineData("https://g/me?access_token=SECRET&after=cursor", "https://g/me?after=cursor")]
    public void Strip_handles_position_variants(string url, string expected)
    {
        Assert.Equal(expected, MetaOAuthService.StripAccessTokenFromQuery(url));
    }

    // ── M4: debug service output is token-free ──────────────────────────────────

    [Fact]
    public async Task DebugInstagramDiscovery_output_has_no_tokens_or_raw_bodies()
    {
        SeedConnection();
        var service = MakeService();

        var result = await service.DebugInstagramDiscoveryAsync(WorkspaceId);
        var json = JsonSerializer.Serialize(result);

        // No secrets, no token prefixes, no raw Graph bodies.
        Assert.DoesNotContain(UserToken, json);
        Assert.DoesNotContain(PageToken, json);
        Assert.DoesNotContain("pageTokenPrefix", json);
        Assert.DoesNotContain("userTokenPrefix", json);
        Assert.DoesNotContain("rawJson", json);

        // Still returns useful, non-sensitive diagnostics.
        Assert.Contains("pageCount", json);
        Assert.Contains("computedResult", json);
        Assert.Contains("pages_show_list", json); // granted permission NAME
    }

    // ── wiring ──────────────────────────────────────────────────────────────────

    private void SeedConnection()
    {
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            Provider = ProviderType.Meta,
            ProviderAccountId = "meta-user-1",
            AccessToken = UserToken,
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsConnected = true,
            Status = ConnectionStatus.Active,
        };
        conn.Pages.Add(new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            MetaConnectionId = conn.Id,
            PageId = PageId,
            Name = "Test Page",
            AccessToken = PageToken,
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
            Status = ConnectionStatus.Active,
        });
        _db.MetaConnections.Add(conn);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private MetaOAuthService MakeService()
    {
        var httpClient = new HttpClient(new DebugGraphFakeHandler());
        var scheduler = new Mock<IPostScheduler>();
        var handler = new MetaProviderLifecycleHandler(
            _db, scheduler.Object, new Mock<ILogger<MetaProviderLifecycleHandler>>().Object);
        var providerConnections = new ProviderConnectionService(
            _db, new IProviderLifecycleHandler[] { handler },
            new Mock<ILogger<ProviderConnectionService>>().Object);

        return new MetaOAuthService(
            _db, httpClient,
            new MetaOptions { AppId = "test", AppSecret = "test", RedirectUri = "http://localhost/cb" },
            new Mock<ILogger<MetaOAuthService>>().Object,
            scheduler.Object, providerConnections,
            new MetaApiOptions(),
            new PublishingOptions { OAuthStateExpirationMinutes = 10 });
    }

    /// <summary>Fake Graph API returning a page token in /me/accounts and IG linkage per page.</summary>
    private sealed class DebugGraphFakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            string json;

            if (url.Contains("/me/permissions"))
            {
                json = JsonSerializer.Serialize(new
                {
                    data = new[]
                    {
                        new { permission = "pages_show_list", status = "granted" },
                        new { permission = "pages_manage_posts", status = "granted" },
                    },
                });
            }
            else if (url.Contains("/me/accounts"))
            {
                json = JsonSerializer.Serialize(new
                {
                    data = new[]
                    {
                        new { id = PageId, name = "Test Page", category = "Software", access_token = PageToken },
                    },
                });
            }
            else if (url.Contains($"/{PageId}?"))
            {
                json = JsonSerializer.Serialize(new
                {
                    name = "Test Page",
                    instagram_business_account = new { id = "ig-1", username = "acme", name = "Acme" },
                });
            }
            else
            {
                json = "{}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }
}
