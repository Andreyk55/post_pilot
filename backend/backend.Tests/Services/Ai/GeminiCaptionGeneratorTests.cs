using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Ai;

public class GeminiCaptionGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_TranslationPrompt_DoesNotIncludeVoiceProfileOrBrandVoiceInstructions()
    {
        var httpHandlerMock = new Mock<HttpMessageHandler>();
        string? requestJson = null;

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
                                    ""captions"": [""שלום""],
                                    ""warnings"": []
                                }"
                            }
                        }
                    }
                }
            }
        };

        httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(geminiResponse), Encoding.UTF8, "application/json")
            });

        var generator = new GeminiCaptionGenerator(
            new HttpClient(httpHandlerMock.Object),
            new GeminiSettings
            {
                ApiKey = "test-api-key",
                Model = "gemini-2.0-flash",
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                TimeoutSeconds = 30
            },
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GeminiCaptionGenerator>.Instance,
            new AiCacheOptions
            {
                CaptionAssistMinutes = 60,
                LanguageDetectionMinutes = 1440,
                GoogleAiClientMinutes = 60,
                PostTimeSuggestionMinutes = 10,
                AssetResolverDownloadUrlExpirationMinutes = 15
            });

        await generator.GenerateAsync(new PostPilot.Api.Services.Ai.CaptionGenerateRequest(
            Text: "Hello",
            SourceLanguage: "en",
            OutputLanguage: "he",
            Platform: AiPlatform.Facebook,
            Variants: 1,
            StrictMeaning: true));

        Assert.NotNull(requestJson);
        using var json = JsonDocument.Parse(requestJson!);
        var prompt = json.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        Assert.NotNull(prompt);
        Assert.Contains("Translate this text from English to Hebrew", prompt);
        Assert.Contains("Preserve the original meaning strictly", prompt);
        Assert.DoesNotContain("brand voice", prompt!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("voice profile", prompt!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("banned words", prompt!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example posts", prompt!, StringComparison.OrdinalIgnoreCase);
    }
}
