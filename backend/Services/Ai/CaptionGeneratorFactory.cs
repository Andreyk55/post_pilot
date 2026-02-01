using Microsoft.Extensions.Caching.Memory;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Factory for creating caption generator instances based on provider configuration.
/// </summary>
public static class CaptionGeneratorFactory
{
    public static ICaptionGenerator Create(
        string provider,
        IServiceProvider serviceProvider)
    {
        return provider.ToLower() switch
        {
            "gemini" => new GeminiCaptionGenerator(
                serviceProvider.GetRequiredService<HttpClient>(),
                serviceProvider.GetRequiredService<GeminiSettings>(),
                serviceProvider.GetRequiredService<IMemoryCache>(),
                serviceProvider.GetRequiredService<ILogger<GeminiCaptionGenerator>>()),
            
            // Future providers can be added here:
            // "openai" => new OpenAICaptionGenerator(...),
            // "anthropic" => new AnthropicCaptionGenerator(...),
            
            _ => throw new InvalidOperationException($"Unknown caption generator provider: {provider}")
        };
    }
}
