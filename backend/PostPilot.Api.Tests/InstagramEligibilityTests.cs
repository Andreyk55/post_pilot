using Xunit;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services;

namespace PostPilot.Api.Tests;

public class InstagramEligibilityTests
{
    [Fact]
    public void MapEligibility_WithLinkedIgAccount_ReturnsConnected()
    {
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = new MetaInstagramAccount
            {
                Id = "ig-123",
                Username = "testuser",
                Name = "Test User",
                ProfilePictureUrl = "https://example.com/pic.jpg"
            }
        };

        var result = MetaOAuthService.MapEligibility("page-1", "My Page", igResponse, false, null);

        Assert.Equal(InstagramEligibilityStatus.Connected, result.EligibilityStatus);
        Assert.Equal("ig-123", result.IgUserId);
        Assert.Equal("testuser", result.IgUsername);
        Assert.Equal("Test User", result.IgDisplayName);
        Assert.Equal("https://example.com/pic.jpg", result.IgProfilePictureUrl);
        Assert.Equal("page-1", result.PageId);
        Assert.Equal("My Page", result.PageName);
    }

    [Fact]
    public void MapEligibility_WithNoIgAccount_ReturnsNotLinked()
    {
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = null
        };

        var result = MetaOAuthService.MapEligibility("page-1", "My Page", igResponse, false, null);

        Assert.Equal(InstagramEligibilityStatus.NotLinked, result.EligibilityStatus);
        Assert.Null(result.IgUserId);
        Assert.Null(result.IgUsername);
        Assert.Contains("No Instagram account is linked", result.Reason);
    }

    [Fact]
    public void MapEligibility_WithNullResponse_ReturnsNotLinked()
    {
        var result = MetaOAuthService.MapEligibility("page-1", "My Page", null, false, null);

        Assert.Equal(InstagramEligibilityStatus.NotLinked, result.EligibilityStatus);
        Assert.Null(result.IgUserId);
    }

    [Fact]
    public void MapEligibility_WithEmptyIgId_ReturnsNotProfessional()
    {
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = new MetaInstagramAccount
            {
                Id = "",
                Username = "personal_user"
            }
        };

        var result = MetaOAuthService.MapEligibility("page-1", "My Page", igResponse, false, null);

        Assert.Equal(InstagramEligibilityStatus.NotProfessional, result.EligibilityStatus);
        Assert.Null(result.IgUserId);
        Assert.Contains("not a Business or Creator account", result.Reason);
    }

    [Fact]
    public void MapEligibility_WhenApiCallFailed_ReturnsUnknown()
    {
        var result = MetaOAuthService.MapEligibility("page-1", "My Page", null, true, null);

        Assert.Equal(InstagramEligibilityStatus.Unknown, result.EligibilityStatus);
        Assert.Null(result.IgUserId);
        Assert.Contains("Could not check", result.Reason);
    }

    [Fact]
    public void MapEligibility_WhenApiCallFailed_WithCustomError_UsesCustomMessage()
    {
        var result = MetaOAuthService.MapEligibility(
            "page-1", "My Page", null, true,
            "Missing Instagram permissions. Reconnect your Meta account to grant Instagram access.");

        Assert.Equal(InstagramEligibilityStatus.Unknown, result.EligibilityStatus);
        Assert.Contains("Missing Instagram permissions", result.Reason);
    }

    [Fact]
    public void MapEligibility_PreservesPageIdAndName()
    {
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = null
        };

        var result = MetaOAuthService.MapEligibility("page-abc-123", "Business Page", igResponse, false, null);

        Assert.Equal("page-abc-123", result.PageId);
        Assert.Equal("Business Page", result.PageName);
    }

    [Fact]
    public void MapEligibility_Connected_HasPositiveReason()
    {
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = new MetaInstagramAccount
            {
                Id = "ig-456",
                Username = "mybusiness"
            }
        };

        var result = MetaOAuthService.MapEligibility("page-1", "My Page", igResponse, false, null);

        Assert.Equal(InstagramEligibilityStatus.Connected, result.EligibilityStatus);
        Assert.Contains("ready", result.Reason);
    }

    [Fact]
    public void MapEligibility_ConnectedInstagramAccount_FallbackWorks()
    {
        // Only connected_instagram_account is set (Creator account scenario)
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = null,
            ConnectedInstagramAccount = new MetaInstagramAccount
            {
                Id = "ig-creator-789",
                Username = "appquestor",
                Name = "App Questor",
                ProfilePictureUrl = "https://example.com/creator.jpg"
            }
        };

        var result = MetaOAuthService.MapEligibility("900141146525294", "Posts Dev Page", igResponse, false, null);

        Assert.Equal(InstagramEligibilityStatus.Connected, result.EligibilityStatus);
        Assert.Equal("ig-creator-789", result.IgUserId);
        Assert.Equal("appquestor", result.IgUsername);
        Assert.Equal("App Questor", result.IgDisplayName);
    }

    [Fact]
    public void MapEligibility_BusinessAccountPreferredOverConnected()
    {
        // Both fields set — instagram_business_account should take priority
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = new MetaInstagramAccount
            {
                Id = "ig-biz-111",
                Username = "bizaccount"
            },
            ConnectedInstagramAccount = new MetaInstagramAccount
            {
                Id = "ig-creator-222",
                Username = "creatoraccount"
            }
        };

        var result = MetaOAuthService.MapEligibility("page-1", "My Page", igResponse, false, null);

        Assert.Equal(InstagramEligibilityStatus.Connected, result.EligibilityStatus);
        Assert.Equal("ig-biz-111", result.IgUserId);
        Assert.Equal("bizaccount", result.IgUsername);
    }

    [Fact]
    public void MapEligibility_BothFieldsNull_ReturnsNotLinked()
    {
        var igResponse = new MetaPageInstagramResponse
        {
            InstagramBusinessAccount = null,
            ConnectedInstagramAccount = null
        };

        var result = MetaOAuthService.MapEligibility("page-1", "My Page", igResponse, false, null);

        Assert.Equal(InstagramEligibilityStatus.NotLinked, result.EligibilityStatus);
        Assert.Null(result.IgUserId);
    }
}
