using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Service for AI-powered post time suggestions.
/// Uses Gemini to suggest optimal posting times based on post content, platform, and audience.
/// </summary>
public class PostTimeSuggestionService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PostTimeSuggestionService> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // Static fallback times when AI fails
    private static readonly Dictionary<AiPlatform, PostTimeSuggestionResponse> FallbackTimes = new()
    {
        [AiPlatform.Facebook] = new PostTimeSuggestionResponse(
            new TimeSuggestion("09:00", "Morning Peak", 70, "Generally good engagement time for Facebook"),
            new List<TimeSuggestion>
            {
                new("13:00", "Lunch Break", 65, "People often check Facebook during lunch"),
                new("19:00", "Evening Wind-down", 60, "After work browsing time")
            }),
        [AiPlatform.Instagram] = new PostTimeSuggestionResponse(
            new TimeSuggestion("11:00", "Late Morning", 70, "High Instagram engagement window"),
            new List<TimeSuggestion>
            {
                new("14:00", "Afternoon Break", 65, "Mid-afternoon Instagram scrolling"),
                new("20:00", "Evening Peak", 60, "Prime evening browsing time")
            }),
        [AiPlatform.LinkedIn] = new PostTimeSuggestionResponse(
            new TimeSuggestion("08:00", "Start of Workday", 75, "Professionals check LinkedIn early"),
            new List<TimeSuggestion>
            {
                new("12:00", "Lunch Hour", 65, "Quick LinkedIn check during lunch"),
                new("17:00", "End of Workday", 55, "Commute and wind-down time")
            }),
        [AiPlatform.X] = new PostTimeSuggestionResponse(
            new TimeSuggestion("09:00", "Morning News", 70, "Twitter/X peaks with morning news cycles"),
            new List<TimeSuggestion>
            {
                new("12:00", "Midday", 65, "Lunch break engagement"),
                new("17:00", "Evening Rush", 60, "After-work news catch-up")
            })
    };

    public PostTimeSuggestionService(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILogger<PostTimeSuggestionService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _cache = cache;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
    }

    public async Task<PostTimeSuggestionResponse> SuggestPostTimeAsync(
        PostTimeSuggestionRequest request,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(request);

        if (_cache.TryGetValue(cacheKey, out PostTimeSuggestionResponse? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for post time suggestion: {Platform}, {Weekday}", request.Platform, request.Weekday);
            return cached;
        }

        try
        {
            var prompt = BuildPrompt(request);
            _logger.LogInformation("Post time suggestion prompt:\n{Prompt}", prompt);
            var responseText = await CallGeminiAsync(prompt, cancellationToken);
            _logger.LogInformation("Post time suggestion response:\n{Response}", responseText);
            var result = ParseResponse(responseText);

            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get AI time suggestion, falling back to static times");
            return GetFallbackResponse(request.Platform);
        }
    }

    private async Task<string> CallGeminiAsync(string prompt, CancellationToken cancellationToken)
    {
        var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

        // Check if model supports JSON mode (Gemma models don't)
        var supportsJsonMode = !_settings.Model.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);

        object generationConfig = supportsJsonMode
            ? new { temperature = 0.7, maxOutputTokens = 1024, responseMimeType = "application/json" }
            : new { temperature = 0.7, maxOutputTokens = 1024 };

        var geminiRequest = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig
        };

        var json = JsonSerializer.Serialize(geminiRequest, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API error for post time suggestion. Status: {StatusCode}, Body: {Body}",
                (int)response.StatusCode, responseBody);
            throw new GeminiApiException($"API error: {response.StatusCode}", (int)response.StatusCode);
        }

        // Parse Gemini response
        using var doc = JsonDocument.Parse(responseBody);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new GeminiApiException("Empty response from Gemini API", 500);
        }

        return text;
    }

    private PostTimeSuggestionResponse ParseResponse(string responseText)
    {
        // Extract JSON from response
        var jsonStart = responseText.IndexOf('{');
        var jsonEnd = responseText.LastIndexOf('}');

        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            throw new GeminiApiException("Invalid JSON response", 500);
        }

        var json = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
        var parsed = JsonSerializer.Deserialize<TimeSuggestionJsonResponse>(json, JsonOptions);

        if (parsed == null)
        {
            throw new GeminiApiException("Failed to parse time suggestion response", 500);
        }

        var primary = new TimeSuggestion(
            parsed.Primary.Time,
            parsed.Primary.Label,
            parsed.Primary.Confidence,
            parsed.Primary.Reason);

        var alternatives = parsed.Alternatives
            .Select(a => new TimeSuggestion(a.Time, a.Label, a.Confidence, a.Reason))
            .ToList();

        return new PostTimeSuggestionResponse(primary, alternatives);
    }

    private static string BuildPrompt(PostTimeSuggestionRequest request)
    {
        var audienceContext = request.AudienceLocation switch
        {
            AudienceLocationMode.MyLocation => "The audience is primarily local (same timezone as the poster).",
            AudienceLocationMode.SpecificCountry => $"The audience is primarily in {request.Country}.",
            AudienceLocationMode.Worldwide => "The audience is spread globally across multiple timezones.",
            _ => "The audience location is unspecified."
        };

        var goalContext = request.Goal switch
        {
            AiGoal.Engage => "The goal is to maximize engagement (likes, comments, shares).",
            AiGoal.Promote => "The goal is to promote a product/service and drive conversions.",
            AiGoal.Announce => "The goal is to announce news/updates and reach maximum audience.",
            AiGoal.Educate => "The goal is to educate the audience with informative content.",
            AiGoal.Story => "The goal is to share a story/experience and connect emotionally.",
            _ => "The goal is general engagement."
        };

        return $@"You are a social media timing expert. Suggest the best times to post based on the context below.

CONTEXT:
- Platform: {request.Platform}
- Day of week: {request.Weekday}
- Poster's timezone: {request.Timezone}
- Audience: {audienceContext}
- Goal: {goalContext}

POST CONTENT (for context on what type of content is being posted):
{request.PostText}

Based on general social media best practices, audience location, and the content/goal, suggest optimal posting times.
IMPORTANT: Return times in the POSTER'S timezone ({request.Timezone}), adjusted so the post reaches the audience at optimal times in THEIR timezone.

Return ONLY valid JSON in this exact format:
{{
  ""primary"": {{
    ""time"": ""HH:MM"",
    ""label"": ""Short descriptive label (2-4 words)"",
    ""confidence"": 85,
    ""reason"": ""Brief explanation why this time is recommended (1 sentence)""
  }},
  ""alternatives"": [
    {{
      ""time"": ""HH:MM"",
      ""label"": ""Short label"",
      ""confidence"": 75,
      ""reason"": ""Brief reason""
    }},
    {{
      ""time"": ""HH:MM"",
      ""label"": ""Short label"",
      ""confidence"": 65,
      ""reason"": ""Brief reason""
    }}
  ]
}}

RULES:
- Times must be in 24-hour format (HH:MM) in the poster's timezone ({request.Timezone})
- Confidence is 0-100 (primary should be highest)
- Provide exactly 2 alternatives
- Labels should be short and descriptive (e.g., ""Morning Peak"", ""Lunch Hour"", ""Evening Prime"")
- Reasons should be brief but informative
- Consider the specific platform's user behavior patterns
- Consider the day of week ({request.Weekday})
- Output ONLY the JSON, no other text";
    }

    private static string BuildCacheKey(PostTimeSuggestionRequest request)
    {
        var keyData = $"{request.Platform}:{request.Goal}:{request.Weekday}:{request.AudienceLocation}:{request.Country ?? ""}:{request.PostText}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyData)))[..16];
        return $"ai:PostTime:{request.Platform}:{request.Goal}:{request.Weekday}:{hash}";
    }

    private static PostTimeSuggestionResponse GetFallbackResponse(AiPlatform platform)
    {
        return FallbackTimes.TryGetValue(platform, out var response)
            ? response
            : FallbackTimes[AiPlatform.Facebook]; // Default fallback
    }

    // JSON response DTOs for parsing
    private class TimeSuggestionJsonResponse
    {
        public TimeSuggestionItem Primary { get; set; } = new();
        public List<TimeSuggestionItem> Alternatives { get; set; } = new();
    }

    private class TimeSuggestionItem
    {
        public string Time { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Confidence { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
