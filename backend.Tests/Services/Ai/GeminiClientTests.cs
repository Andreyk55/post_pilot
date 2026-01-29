using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Ai;
using Xunit;

namespace PostPilot.Api.Tests.Services.Ai;

public class GeminiClientTests
{
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly GeminiSettings _settings;
    private readonly GeminiClient _client;

    public GeminiClientTests()
    {
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _settings = new GeminiSettings
        {
            ApiKey = "test-api-key",
            Model = "gemini-2.0-flash",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            TimeoutSeconds = 30
        };

        _client = new GeminiClient(
            _httpClient,
            _settings,
            _cache,
            NullLogger<GeminiClient>.Instance);
    }

    [Fact]
    public async Task GenerateVariantsAsync_ValidResponse_ReturnsVariants()
    {
        var geminiResponse = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = @"{
                                    ""variants"": [
                                        { ""title"": ""Option 1"", ""text"": ""Polished text 1"" },
                                        { ""title"": ""Option 2"", ""text"": ""Polished text 2"" },
                                        { ""title"": ""Option 3"", ""text"": ""Polished text 3"" }
                                    ]
                                }"
                            }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var result = await _client.GenerateVariantsAsync(
            AiTextAction.Polish,
            AiPlatform.Facebook,
            "Original text",
            null,
            "en");

        Assert.Equal(AiTextAction.Polish, result.Action);
        Assert.Equal(3, result.Variants.Count);
        Assert.Equal("Option 1", result.Variants[0].Title);
        Assert.Equal("Polished text 1", result.Variants[0].Text);
    }

    [Fact]
    public async Task GenerateHashtagsAsync_ValidResponse_ReturnsHashtags()
    {
        var geminiResponse = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = @"{
                                    ""hashtags"": [""#social"", ""#media"", ""#marketing""]
                                }"
                            }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var result = await _client.GenerateHashtagsAsync(
            AiPlatform.Instagram,
            "Test post about social media",
            "en");

        Assert.Equal(AiTextAction.Hashtags, result.Action);
        Assert.Equal(3, result.Hashtags.Count);
        Assert.Contains("#social", result.Hashtags);
    }

    [Fact]
    public async Task RunPreFlightCheckAsync_ValidResponse_ReturnsScoreAndIssues()
    {
        var geminiResponse = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = @"{
                                    ""score"": 75,
                                    ""issues"": [
                                        { ""severity"": ""warning"", ""message"": ""Too long"", ""suggestedFix"": ""Shorten it"" },
                                        { ""severity"": ""info"", ""message"": ""Add hashtags"", ""suggestedFix"": null }
                                    ]
                                }"
                            }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var result = await _client.RunPreFlightCheckAsync(
            AiPlatform.X,
            "Test post",
            "en");

        Assert.Equal(AiTextAction.PreFlight, result.Action);
        Assert.Equal(75, result.Score);
        Assert.Equal(2, result.Issues.Count);
        Assert.Equal(AiIssueSeverity.Warning, result.Issues[0].Severity);
        Assert.Equal("Too long", result.Issues[0].Message);
    }

    [Fact]
    public async Task GenerateVariantsAsync_RateLimitExceeded_ThrowsGeminiApiException()
    {
        SetupHttpResponse(HttpStatusCode.TooManyRequests, "Rate limit exceeded");

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            _client.GenerateVariantsAsync(
                AiTextAction.Polish,
                AiPlatform.Facebook,
                "Test",
                null,
                "en"));

        Assert.Equal(429, exception.StatusCode);
    }

    [Fact]
    public async Task GenerateVariantsAsync_Unauthorized_ThrowsGeminiApiException()
    {
        SetupHttpResponse(HttpStatusCode.Unauthorized, "Invalid API key");

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            _client.GenerateVariantsAsync(
                AiTextAction.Polish,
                AiPlatform.Facebook,
                "Test",
                null,
                "en"));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public async Task GenerateVariantsAsync_CachesResponse()
    {
        var geminiResponse = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = @"{
                                    ""variants"": [
                                        { ""title"": ""Option 1"", ""text"": ""Cached text"" },
                                        { ""title"": ""Option 2"", ""text"": ""Cached text 2"" },
                                        { ""title"": ""Option 3"", ""text"": ""Cached text 3"" }
                                    ]
                                }"
                            }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        // First call
        var result1 = await _client.GenerateVariantsAsync(
            AiTextAction.Polish,
            AiPlatform.Facebook,
            "Same text",
            null,
            "en");

        // Second call with same parameters - should hit cache
        var result2 = await _client.GenerateVariantsAsync(
            AiTextAction.Polish,
            AiPlatform.Facebook,
            "Same text",
            null,
            "en");

        // Both results should be the same and HTTP should only be called once
        Assert.Equal(result1.Variants[0].Text, result2.Variants[0].Text);
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GenerateVariantsAsync_DifferentInputs_NoCacheHit()
    {
        var geminiResponse = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new { text = @"{ ""variants"": [{ ""title"": ""A"", ""text"": ""T1"" }, { ""title"": ""B"", ""text"": ""T2"" }, { ""title"": ""C"", ""text"": ""T3"" }] }" }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        // Call with different text
        await _client.GenerateVariantsAsync(AiTextAction.Polish, AiPlatform.Facebook, "Text 1", null, "en");
        await _client.GenerateVariantsAsync(AiTextAction.Polish, AiPlatform.Facebook, "Text 2", null, "en");

        // HTTP should be called twice
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}
