using System.Text.RegularExpressions;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram user tag validation rules (username regex + position bounds).
/// These mirror the validation logic in PostsController.CreatePost.
/// </summary>
public class InstagramUserTagTests
{
    private static readonly Regex UsernameRegex = new(@"^[A-Za-z0-9._]{1,30}$");

    // Username validation

    [Theory]
    [InlineData("nike")]
    [InlineData("john_doe")]
    [InlineData("john.doe")]
    [InlineData("john_doe.99")]
    [InlineData("a")]
    [InlineData("A1")]
    public void ValidUsernames_Match(string username)
    {
        Assert.Matches(UsernameRegex, username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("bad!name")]
    [InlineData("@nike")]
    [InlineData("nike/")]
    public void InvalidUsernames_DoNotMatch(string username)
    {
        Assert.DoesNotMatch(UsernameRegex, username);
    }

    [Fact]
    public void Username_Over30Chars_DoesNotMatch()
    {
        var longUsername = new string('a', 31);
        Assert.DoesNotMatch(UsernameRegex, longUsername);
    }

    [Fact]
    public void Username_Exactly30Chars_Matches()
    {
        var username = new string('a', 30);
        Assert.Matches(UsernameRegex, username);
    }

    // Position bounds validation

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 0.0)]
    public void ValidPositions_AreInBounds(double x, double y)
    {
        Assert.True(x >= 0 && x <= 1 && y >= 0 && y <= 1);
    }

    [Theory]
    [InlineData(-0.1, 0.5)]
    [InlineData(0.5, -0.1)]
    [InlineData(1.1, 0.5)]
    [InlineData(0.5, 1.1)]
    [InlineData(-1.0, -1.0)]
    public void InvalidPositions_AreOutOfBounds(double x, double y)
    {
        Assert.False(x >= 0 && x <= 1 && y >= 0 && y <= 1);
    }

    // JSON serialization format

    [Fact]
    public void UserTagsJson_Format_MatchesExpected()
    {
        var tags = new[]
        {
            new { username = "nike", x = 0.52, y = 0.33 },
            new { username = "adidas", x = 0.1, y = 0.9 },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(tags,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        Assert.Contains("\"username\":\"nike\"", json);
        Assert.Contains("\"x\":0.52", json);
        Assert.Contains("\"y\":0.33", json);
        Assert.Contains("\"username\":\"adidas\"", json);
    }
}
