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

    [Fact]
    public async Task GenerateCreatorVariantsAsync_ValidResponse_ReturnsVariants()
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
                                        { ""id"": ""v1"", ""text"": ""Engaging post with emoji!"" },
                                        { ""id"": ""v2"", ""text"": ""Another engaging variant"" },
                                        { ""id"": ""v3"", ""text"": ""Third creative option"" }
                                    ]
                                }"
                            }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.Facebook,
            InputText: "Check out our new product!",
            Goal: AiGoal.Engage,
            Tone: AiTone.Casual,
            Length: AiLength.Medium,
            IncludeEmojis: true,
            IncludeHashtags: false,
            IncludeCta: true,
            IncludeQuestion: false,
            NumVariants: 3
        );

        var result = await _client.GenerateCreatorVariantsAsync(request);

        Assert.Equal(3, result.Variants.Count);
        Assert.Equal("v1", result.Variants[0].Id);
        Assert.Equal("Engaging post with emoji!", result.Variants[0].Text);
    }

    [Fact]
    public async Task GenerateCreatorVariantsAsync_WithRegenerateIndex_ReturnsOneVariant()
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
                                        { ""id"": ""regen1"", ""text"": ""Regenerated variant"" }
                                    ]
                                }"
                            }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.Instagram,
            InputText: "Original text",
            Goal: AiGoal.Promote,
            Tone: AiTone.Professional,
            Length: AiLength.Short,
            NumVariants: 1,
            RegenerateIndex: 0
        );

        var result = await _client.GenerateCreatorVariantsAsync(request);

        Assert.Single(result.Variants);
        Assert.Equal("Regenerated variant", result.Variants[0].Text);
    }

    [Theory]
    [InlineData(AiGoal.Engage)]
    [InlineData(AiGoal.Promote)]
    [InlineData(AiGoal.Announce)]
    [InlineData(AiGoal.Educate)]
    [InlineData(AiGoal.Story)]
    public async Task GenerateCreatorVariantsAsync_AllGoals_ProcessesSuccessfully(AiGoal goal)
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
                            new { text = @"{ ""variants"": [{ ""id"": ""1"", ""text"": ""Test"" }] }" }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.LinkedIn,
            InputText: $"Test input for {goal}",
            Goal: goal,
            Tone: AiTone.Professional,
            Length: AiLength.Medium,
            NumVariants: 1
        );

        var result = await _client.GenerateCreatorVariantsAsync(request);
        Assert.NotEmpty(result.Variants);
    }

    [Theory]
    [InlineData(AiLength.Short)]
    [InlineData(AiLength.Medium)]
    [InlineData(AiLength.Long)]
    public async Task GenerateCreatorVariantsAsync_AllLengths_ProcessesSuccessfully(AiLength length)
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
                            new { text = @"{ ""variants"": [{ ""id"": ""1"", ""text"": ""Test"" }] }" }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.X,
            InputText: $"Test input for {length}",
            Goal: AiGoal.Engage,
            Tone: AiTone.Casual,
            Length: length,
            NumVariants: 1
        );

        var result = await _client.GenerateCreatorVariantsAsync(request);
        Assert.NotEmpty(result.Variants);
    }

    [Fact]
    public async Task GenerateCreatorVariantsAsync_WithAllIncludeFlags_ProcessesSuccessfully()
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
                            new { text = @"{ ""variants"": [{ ""id"": ""1"", ""text"": ""Full featured post!"" }] }" }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.Instagram,
            InputText: "My awesome product",
            Goal: AiGoal.Promote,
            Tone: AiTone.Inspirational,
            Length: AiLength.Medium,
            IncludeEmojis: true,
            IncludeHashtags: true,
            IncludeCta: true,
            IncludeQuestion: true,
            NumVariants: 3
        );

        var result = await _client.GenerateCreatorVariantsAsync(request);

        Assert.NotEmpty(result.Variants);
    }

    [Fact]
    public async Task GenerateCreatorVariantsAsync_EmptyVariantsArray_ThrowsException()
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
                            new { text = @"{ ""variants"": [] }" }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.Facebook,
            InputText: "Test",
            Goal: AiGoal.Engage,
            Tone: AiTone.Professional,
            Length: AiLength.Medium,
            NumVariants: 3
        );

        await Assert.ThrowsAsync<GeminiApiException>(() =>
            _client.GenerateCreatorVariantsAsync(request));
    }

    [Fact]
    public async Task GenerateCreatorVariantsAsync_SkipsCache_WhenRegenerating()
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
                            new { text = @"{ ""variants"": [{ ""id"": ""1"", ""text"": ""Fresh"" }] }" }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.Facebook,
            InputText: "Same text",
            Goal: AiGoal.Engage,
            Tone: AiTone.Professional,
            Length: AiLength.Medium,
            NumVariants: 1,
            RegenerateIndex: 0
        );

        // Call twice with regenerate index - should NOT cache
        await _client.GenerateCreatorVariantsAsync(request);
        await _client.GenerateCreatorVariantsAsync(request);

        // HTTP should be called twice (no caching for regenerate)
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GenerateCreatorVariantsAsync_TruncatedJson_SalvagesPartialVariants()
    {
        // Simulate truncated JSON response (what Gemini might return if it runs out of tokens)
        var truncatedJson = @"{
            ""variants"": [
                { ""id"": ""v1"", ""text"": ""First complete variant"" },
                { ""id"": ""v2"", ""text"": ""Second complete variant"" },
                { ""id"": ""v3"", ""text"": ""Third vari";  // Truncated mid-variant

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
                            new { text = truncatedJson }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.Facebook,
            InputText: "Test text",
            Goal: AiGoal.Engage,
            Tone: AiTone.Professional,
            Length: AiLength.Medium,
            NumVariants: 3
        );

        var result = await _client.GenerateCreatorVariantsAsync(request);

        // Should salvage the 2 complete variants
        Assert.Equal(2, result.Variants.Count);
        Assert.Equal("First complete variant", result.Variants[0].Text);
        Assert.Equal("Second complete variant", result.Variants[1].Text);
    }

    [Fact]
    public async Task GenerateCreatorVariantsAsync_TruncatedJsonNoCompleteVariants_ThrowsException()
    {
        // Simulate truncated JSON with no complete variants
        var truncatedJson = @"{
            ""variants"": [
                { ""id"": ""v1"", ""text"": ""Incom";  // Truncated immediately

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
                            new { text = truncatedJson }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var request = new AiGenerateVariantsRequest(
            Platform: AiPlatform.Facebook,
            InputText: "Test text",
            Goal: AiGoal.Engage,
            Tone: AiTone.Professional,
            Length: AiLength.Medium,
            NumVariants: 3
        );

        // Should throw because no complete variants could be salvaged
        await Assert.ThrowsAsync<GeminiApiException>(() =>
            _client.GenerateCreatorVariantsAsync(request));
    }

    [Fact]
    public async Task PreFlightCheckAsync_TruncatedJson_SalvagesPartialIssues()
    {
        // Simulate truncated JSON with 2 complete issues and 1 incomplete
        var truncatedJson = @"{
            ""score"": 75,
            ""issues"": [
                { ""severity"": ""Warning"", ""message"": ""Post is quite long"", ""suggestedFix"": ""Consider shortening"" },
                { ""severity"": ""Info"", ""message"": ""No hashtags detected"", ""suggestedFix"": ""Add relevant hashtags"" },
                { ""severity"": ""Error"", ""message"": ""Missing call to action"", ""suggestedFix"": ""Add a CTA like 'Learn more at";  // Truncated

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
                            new { text = truncatedJson }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var result = await _client.PreFlightCheckAsync(
            AiPlatform.Facebook,
            "This is a test post without hashtags or CTA",
            "en");

        // Should salvage the score and 2 complete issues
        Assert.Equal(75, result.Score);
        Assert.Equal(2, result.Issues.Count);
        Assert.Equal(AiIssueSeverity.Warning, result.Issues[0].Severity);
        Assert.Equal("Post is quite long", result.Issues[0].Message);
        Assert.Equal(AiIssueSeverity.Info, result.Issues[1].Severity);
        Assert.Equal("No hashtags detected", result.Issues[1].Message);
    }

    [Fact]
    public async Task PreFlightCheckAsync_TruncatedJsonScoreOnly_ReturnsScoreWithNoIssues()
    {
        // Simulate truncated JSON with only the score complete
        var truncatedJson = @"{
            ""score"": 90,
            ""issues"": [
                { ""severity"": ""Info"", ""message"": ""Trun";  // Truncated immediately

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
                            new { text = truncatedJson }
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(geminiResponse));

        var result = await _client.PreFlightCheckAsync(
            AiPlatform.Facebook,
            "Short test post",
            "en");

        // Should salvage the score with empty issues
        Assert.Equal(90, result.Score);
        Assert.Empty(result.Issues);
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
