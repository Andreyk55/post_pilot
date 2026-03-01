using Xunit;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Tests.Services;

public class CaptionAssistServiceTests
{
    [Fact]
    public void ExtractNumbers_ShouldFindAllNumbers()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var text = "We have 50 items, priced at 29.99 each, total 1,499.50";

        // Act
        var numbers = InvokePrivateMethod<HashSet<string>>(service, "ExtractNumbers", text);

        // Assert
        Assert.Contains("50", numbers);
        Assert.Contains("29.99", numbers);
        Assert.Contains("1,499.50", numbers);
    }

    [Fact]
    public void ExtractCurrencies_ShouldFindAllCurrencyAmounts()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var text = "Save $50 today! Originally €100, now only ₪250";

        // Act
        var currencies = InvokePrivateMethod<HashSet<string>>(service, "ExtractCurrencies", text);

        // Assert
        Assert.Contains("$50", currencies);
        Assert.Contains("€100", currencies);
        Assert.Contains("₪250", currencies);
    }

    [Fact]
    public void ExtractPercentages_ShouldFindAllPercentages()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var text = "Get 25% off! Limited time 50% discount on selected items";

        // Act
        var percentages = InvokePrivateMethod<HashSet<string>>(service, "ExtractPercentages", text);

        // Assert
        Assert.Contains("25%", percentages);
        Assert.Contains("50%", percentages);
    }

    [Fact]
    public void ExtractUrls_ShouldFindAllUrls()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var text = "Visit https://example.com or www.mysite.com for more info";

        // Act
        var urls = InvokePrivateMethod<HashSet<string>>(service, "ExtractUrls", text);

        // Assert
        Assert.Contains("https://example.com", urls);
        Assert.Contains("www.mysite.com", urls);
    }

    [Fact]
    public void ExtractHashtags_ShouldFindAllHashtags()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var text = "Check out our #sale #newcollection #חדש";

        // Act
        var hashtags = InvokePrivateMethod<HashSet<string>>(service, "ExtractHashtags", text);

        // Assert
        Assert.Contains("#sale", hashtags);
        Assert.Contains("#newcollection", hashtags);
        Assert.Contains("#חדש", hashtags);
    }

    [Fact]
    public void ExtractMentions_ShouldFindAllMentions()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var text = "Thanks @johndoe and @janedoe for the support!";

        // Act
        var mentions = InvokePrivateMethod<HashSet<string>>(service, "ExtractMentions", text);

        // Assert
        Assert.Contains("@johndoe", mentions);
        Assert.Contains("@janedoe", mentions);
    }

    [Fact]
    public void ValidateExactMatch_WithIdenticalSets_ShouldReturnTrue()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var set1 = new HashSet<string> { "item1", "item2", "item3" };
        var set2 = new HashSet<string> { "item1", "item2", "item3" };

        // Act
        var result = InvokePrivateMethod<bool>(service, "ValidateExactMatch", set1, set2);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ValidateExactMatch_WithDifferentSets_ShouldReturnFalse()
    {
        // Arrange
        var service = CreateCaptionAssistService();
        var set1 = new HashSet<string> { "item1", "item2" };
        var set2 = new HashSet<string> { "item1", "item3" };

        // Act
        var result = InvokePrivateMethod<bool>(service, "ValidateExactMatch", set1, set2);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("מוצר חדש ב-₪250 במבצע 20% הנחה! #sale @brand", "he")] // Hebrew with price, percentage, hashtag, mention
    [InlineData("Новый продукт за €100! Скидка 15% на сайте www.example.com #новинка", "ru")] // Russian with currency, percentage, URL, hashtag
    [InlineData("New product for $50! Visit https://example.com #newarrival @store", "en")] // English with all elements
    public void MixedLanguageInput_ShouldPreserveAllCriticalElements(string input, string expectedLang)
    {
        // This is an integration test that would require a real or mocked AI service
        // For now, we test the extraction methods work correctly with multilingual text
        var service = CreateCaptionAssistService();

        // Act - Extract all critical elements
        var numbers = InvokePrivateMethod<HashSet<string>>(service, "ExtractNumbers", input);
        var currencies = InvokePrivateMethod<HashSet<string>>(service, "ExtractCurrencies", input);
        var percentages = InvokePrivateMethod<HashSet<string>>(service, "ExtractPercentages", input);
        var urls = InvokePrivateMethod<HashSet<string>>(service, "ExtractUrls", input);
        var hashtags = InvokePrivateMethod<HashSet<string>>(service, "ExtractHashtags", input);
        var mentions = InvokePrivateMethod<HashSet<string>>(service, "ExtractMentions", input);

        // Assert - Verify elements are found regardless of language
        Assert.NotEmpty(currencies.Count > 0 ? currencies : numbers); // Either currency or number should be found
        if (input.Contains('%')) Assert.NotEmpty(percentages);
        if (input.Contains("http") || input.Contains("www")) Assert.NotEmpty(urls);
        if (input.Contains('#')) Assert.NotEmpty(hashtags);
        if (input.Contains('@')) Assert.NotEmpty(mentions);
    }

    // Helper methods
    private CaptionAssistService CreateCaptionAssistService()
    {
        // Create a minimal service instance for testing extraction methods
        // In real tests, you would use proper dependency injection and mocking
        var languageService = new MockLanguageService();
        var captionGenerator = new MockCaptionGenerator();
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<CaptionAssistService>();

        var cacheOptions = new AiCacheOptions
        {
            CaptionAssistMinutes = 60,
            LanguageDetectionMinutes = 1440,
            GoogleAiClientMinutes = 60,
            PostTimeSuggestionMinutes = 10,
            AssetResolverDownloadUrlExpirationMinutes = 15
        };

        return new CaptionAssistService(languageService, captionGenerator, cache, logger, cacheOptions);
    }

    private T InvokePrivateMethod<T>(object obj, string methodName, params object[] parameters)
    {
        var method = obj.GetType()
            .GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (method == null)
            throw new InvalidOperationException($"Method {methodName} not found");

        return (T)method.Invoke(obj, parameters)!;
    }

    // Mock implementations for testing
    private class MockLanguageService : LanguageService
    {
        public MockLanguageService() 
            : base(new MockLanguageDetector(), new Microsoft.Extensions.Logging.Abstractions.NullLogger<LanguageService>())
        {
        }
    }

    private class MockLanguageDetector : ILanguageDetector
    {
        public Task<LanguageDetectResult> DetectAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LanguageDetectResult("en", 0.95, true));
        }
    }

    private class MockCaptionGenerator : ICaptionGenerator
    {
        public Task<CaptionGenerateResult> GenerateAsync(CaptionGenerateRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CaptionGenerateResult(new[] { "Mock caption" }, Array.Empty<string>()));
        }
    }
}
