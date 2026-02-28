using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Factory for creating language detector instances based on provider configuration.
/// </summary>
public static class LanguageDetectorFactory
{
    public static ILanguageDetector Create(
        string provider,
        IServiceProvider serviceProvider)
    {
        return provider.ToLower() switch
        {
            "gemini" => new GeminiLanguageDetector(
                serviceProvider.GetRequiredService<HttpClient>(),
                serviceProvider.GetRequiredService<GeminiSettings>(),
                serviceProvider.GetRequiredService<IMemoryCache>(),
                serviceProvider.GetRequiredService<ILogger<GeminiLanguageDetector>>(),
                serviceProvider.GetRequiredService<AiCacheOptions>()),
            
            // Future providers can be added here:
            // "local" => new LocalLanguageDetector(...),
            // "openai" => new OpenAILanguageDetector(...),
            
            _ => throw new InvalidOperationException($"Unknown language detector provider: {provider}")
        };
    }
}
