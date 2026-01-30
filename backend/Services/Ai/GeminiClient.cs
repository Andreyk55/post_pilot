using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Ai;

public class GeminiClient : IGeminiClient
{
    private readonly HttpClient _httpClient;
    private readonly GeminiSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GeminiClient> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public GeminiClient(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILogger<GeminiClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _cache = cache;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
    }

    public async Task<AiTextVariantsResponse> GenerateVariantsAsync(
        AiTextAction action,
        AiPlatform platform,
        string text,
        AiTone? tone,
        string language,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(action.ToString(), platform.ToString(), tone?.ToString() ?? "", language, text);

        if (_cache.TryGetValue(cacheKey, out AiTextVariantsResponse? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for variants: {Action}, {Platform}", action, platform);
            return cached;
        }

        var prompt = BuildVariantsPrompt(action, platform, text, tone, language);
        var responseText = await CallGeminiAsync(prompt, cancellationToken);
        var result = ParseVariantsResponse(responseText, action);

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiHashtagsResponse> GenerateHashtagsAsync(
        AiPlatform platform,
        string text,
        string language,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey("Hashtags", platform.ToString(), "", language, text);

        if (_cache.TryGetValue(cacheKey, out AiHashtagsResponse? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for hashtags: {Platform}", platform);
            return cached;
        }

        var prompt = BuildHashtagsPrompt(platform, text, language);
        var responseText = await CallGeminiAsync(prompt, cancellationToken);
        var result = ParseHashtagsResponse(responseText);

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiPreFlightResponse> RunPreFlightCheckAsync(
        AiPlatform platform,
        string text,
        string language,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey("PreFlight", platform.ToString(), "", language, text);

        if (_cache.TryGetValue(cacheKey, out AiPreFlightResponse? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for pre-flight: {Platform}", platform);
            return cached;
        }

        var prompt = BuildPreFlightPrompt(platform, text, language);
        var responseText = await CallGeminiAsync(prompt, cancellationToken);
        var result = ParsePreFlightResponse(responseText);

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    #region Vision API Methods

    public async Task<AiMediaCaptionIdeasResponse> GenerateImageCaptionIdeasAsync(
        byte[] imageBytes,
        string imageMimeType,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default)
    {
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes))[..16];
        var cacheKey = BuildCacheKey("ImageCaption", platform.ToString(), "", language, $"{imageHash}:{existingText ?? ""}");

        if (_cache.TryGetValue(cacheKey, out AiMediaCaptionIdeasResponse? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for image caption: {Platform}", platform);
            return cached;
        }

        var prompt = BuildImageCaptionPrompt(platform, existingText, language);
        var responseText = await CallGeminiVisionAsync(prompt, imageBytes, imageMimeType, maxOutputTokens: 512, cancellationToken: cancellationToken);
        var result = ParseImageCaptionResponse(responseText);

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiImageQualityCheckResponse> CheckImageQualityAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes))[..16];
        var cacheKey = $"ai:ImageQuality:{imageHash}";

        if (_cache.TryGetValue(cacheKey, out AiImageQualityCheckResponse? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for image quality check");
            return cached;
        }

        var prompt = BuildImageQualityPrompt();
        var responseText = await CallGeminiVisionAsync(prompt, imageBytes, imageMimeType, maxOutputTokens: 512, cancellationToken: cancellationToken);
        var result = ParseImageQualityResponse(responseText);

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiAltTextResponse> GenerateAltTextAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes))[..16];
        var cacheKey = $"ai:AltText:{imageHash}";

        if (_cache.TryGetValue(cacheKey, out AiAltTextResponse? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for alt text");
            return cached;
        }

        var prompt = BuildAltTextPrompt();
        var responseText = await CallGeminiVisionAsync(prompt, imageBytes, imageMimeType, maxOutputTokens: 256, cancellationToken: cancellationToken);
        var result = ParseAltTextResponse(responseText);

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    #endregion

    private async Task<string> CallGeminiAsync(string prompt, CancellationToken cancellationToken)
    {
        var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

        var request = new GeminiRequest
        {
            Contents = new[]
            {
                new GeminiContent
                {
                    Parts = new[] { new GeminiPart { Text = prompt } }
                }
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.7,
                MaxOutputTokens = 2048,
                ResponseMimeType = "application/json"
            }
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            stopwatch.Stop();

            _logger.LogInformation("Gemini API call completed in {ElapsedMs}ms, Status: {StatusCode}",
                stopwatch.ElapsedMilliseconds, (int)response.StatusCode);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogError("Gemini API authentication failed");
                throw new GeminiApiException("API key is invalid or misconfigured", (int)response.StatusCode);
            }

             if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
                                ?? response.Headers.RetryAfter?.Date?.Subtract(DateTimeOffset.UtcNow).TotalSeconds;

                _logger.LogWarning("Gemini 429. RetryAfterSec={RetryAfter}. Body={Body}", retryAfter, body);

                throw new GeminiApiException("Gemini rate limit exceeded", 429);
            }

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonOptions);

            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new GeminiApiException("Empty response from Gemini API", 500);
            }

            return text;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Gemini API request timed out after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            throw new GeminiApiException("Request timed out", 504);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error calling Gemini API");
            throw new GeminiApiException($"Failed to connect to AI service: {ex.Message}", 503);
        }
    }

    private async Task<string> CallGeminiVisionAsync(
        string prompt,
        byte[] imageBytes,
        string imageMimeType,
        int maxOutputTokens = 512,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

        // Build request with image inline data
        var base64Image = Convert.ToBase64String(imageBytes);

        var request = new GeminiVisionRequest
        {
            Contents = new[]
            {
                new GeminiVisionContent
                {
                    Parts = new object[]
                    {
                        new GeminiVisionInlineData
                        {
                            InlineData = new GeminiInlineDataContent
                            {
                                MimeType = imageMimeType,
                                Data = base64Image
                            }
                        },
                        new GeminiPart { Text = prompt }
                    }
                }
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.7,
                MaxOutputTokens = maxOutputTokens,
                ResponseMimeType = "application/json"
            }
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            stopwatch.Stop();

            _logger.LogInformation("Gemini Vision API call completed in {ElapsedMs}ms, Status: {StatusCode}",
                stopwatch.ElapsedMilliseconds, (int)response.StatusCode);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogError("Gemini API authentication failed");
                throw new GeminiApiException("API key is invalid or misconfigured", (int)response.StatusCode);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Gemini 429 for vision. Body={Body}", body);
                throw new GeminiApiException("Gemini rate limit exceeded", 429);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Gemini 400 for vision. Body={Body}", body);

                if (body.Contains("size") || body.Contains("large"))
                {
                    throw new GeminiApiException("Image too large for AI processing", 413);
                }

                throw new GeminiApiException("Invalid image or request format", 400);
            }

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonOptions);

            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new GeminiApiException("Empty response from Gemini Vision API", 500);
            }

            return text;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Gemini Vision API request timed out after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            throw new GeminiApiException("Request timed out", 504);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error calling Gemini Vision API");
            throw new GeminiApiException($"Failed to connect to AI service: {ex.Message}", 503);
        }
    }

    private string BuildVariantsPrompt(AiTextAction action, AiPlatform platform, string text, AiTone? tone, string language)
    {
        var maxLength = platform == AiPlatform.X ? 280 : 2000;
        var toneStr = tone?.ToString().ToLower() ?? "professional";
        var actionInstruction = action switch
        {
            AiTextAction.Polish => $"Polish this text in a {toneStr} tone: fix grammar, improve clarity, remove repetition. Keep the original meaning but ensure the tone is {toneStr}.",
            AiTextAction.RewriteTone => $"Rewrite this text in a {toneStr} tone.",
            AiTextAction.Shorten => $"Shorten this text while keeping it {toneStr}. Be concise but maintain the {toneStr} tone.",
            AiTextAction.Expand => $"Expand this text in a {toneStr} tone with more detail, examples, and a call-to-action.",
            _ => throw new ArgumentException($"Invalid action for variants: {action}")
        };

        return $@"You are a social media content assistant. {actionInstruction}

Platform: {platform}
Language: {language}
Max characters per variant: {maxLength}

Original text:
{text}

Generate exactly 3 distinct variants. Return ONLY valid JSON in this exact format:
{{
  ""variants"": [
    {{ ""title"": ""Option 1"", ""text"": ""..."" }},
    {{ ""title"": ""Option 2"", ""text"": ""..."" }},
    {{ ""title"": ""Option 3"", ""text"": ""..."" }}
  ]
}}

Rules:
- Each variant must be under {maxLength} characters
- Keep variants distinct from each other
- Maintain the core message
- Use appropriate style for {platform}
- Output ONLY the JSON, no other text";
    }

    private string BuildHashtagsPrompt(AiPlatform platform, string text, string language)
    {
        var maxHashtags = platform switch
        {
            AiPlatform.Instagram => 30,
            AiPlatform.LinkedIn => 5,
            AiPlatform.X => 3,
            _ => 10
        };

        return $@"You are a social media hashtag expert. Suggest relevant hashtags for this post.

Platform: {platform}
Language: {language}
Max hashtags: {maxHashtags}

Post text:
{text}

Return ONLY valid JSON in this exact format:
{{
  ""hashtags"": [""#tag1"", ""#tag2"", ""#tag3""]
}}

Rules:
- Suggest 5-{maxHashtags} relevant hashtags
- Include mix of popular and niche hashtags
- All hashtags must start with #
- Use {language} language hashtags primarily
- Consider {platform} best practices
- Output ONLY the JSON, no other text";
    }

    private string BuildPreFlightPrompt(AiPlatform platform, string text, string language)
    {
        var charLimit = platform == AiPlatform.X ? 280 : 2000;

        return $@"You are a social media content reviewer. Analyze this post and provide a quality score and issues.

Platform: {platform}
Language: {language}
Character limit: {charLimit}
Current length: {text.Length} characters

Post text:
{text}

Return ONLY valid JSON in this exact format:
{{
  ""score"": 85,
  ""issues"": [
    {{ ""severity"": ""warning"", ""message"": ""..."", ""suggestedFix"": ""..."" }},
    {{ ""severity"": ""info"", ""message"": ""..."", ""suggestedFix"": null }}
  ]
}}

Check for:
- Grammar and spelling errors (severity: error)
- Character limit violations (severity: error)
- Missing call-to-action (severity: info)
- Readability issues (severity: warning)
- Engagement optimization (severity: info)
- Platform-specific best practices (severity: info)
- Overuse of caps or punctuation (severity: warning)
- Missing hashtags if appropriate (severity: info)

Rules:
- Score 0-100 based on overall quality
- Return 3-8 issues, sorted by severity (error > warning > info)
- severity must be one of: ""info"", ""warning"", ""error""
- suggestedFix can be null if no specific fix
- Output ONLY the JSON, no other text";
    }

    #region Vision Prompt Builders

    private static string BuildImageCaptionPrompt(AiPlatform platform, string? existingText, string language)
    {
        var maxLength = platform == AiPlatform.X ? 280 : 2000;
        var existingTextSection = string.IsNullOrWhiteSpace(existingText)
            ? ""
            : $@"
The user has already written this text for the post:
{existingText}

Consider this context when generating captions (they can complement, expand, or provide alternatives to the existing text).";

        return $@"You are a social media content expert. Analyze this image and generate 3 engaging caption ideas for a {platform} post.

Platform: {platform}
Language: {language}
Max caption length: {maxLength} characters
{existingTextSection}

Return ONLY valid JSON in this exact format:
{{
  ""variants"": [
    {{ ""title"": ""Option 1"", ""text"": ""..."" }},
    {{ ""title"": ""Option 2"", ""text"": ""..."" }},
    {{ ""title"": ""Option 3"", ""text"": ""..."" }}
  ]
}}

Rules:
- Generate exactly 3 distinct caption variants
- Each caption must be under {maxLength} characters
- Captions should be engaging and relevant to the image content
- Include relevant emojis where appropriate for {platform}
- Consider {platform} best practices (hashtags for Instagram, professional tone for LinkedIn, etc.)
- Vary the style: one professional, one casual/fun, one with a call-to-action
- Output ONLY the JSON, no other text";
    }

    private static string BuildImageQualityPrompt()
    {
        return @"You are an image quality analyst for social media. Analyze this image for quality issues that might affect its performance on social media.

Return ONLY valid JSON in this exact format:
{
  ""score"": 85,
  ""issues"": [
    { ""severity"": ""warning"", ""message"": ""..."", ""suggestedFix"": ""..."" },
    { ""severity"": ""info"", ""message"": ""..."", ""suggestedFix"": null }
  ]
}

Check for these issues:
- Blurriness or out-of-focus areas (severity: error if severe, warning if minor)
- Low resolution / pixelation (severity: error if very low, warning if borderline)
- Poor lighting / too dark / too bright (severity: warning)
- Text readability - if image contains text, is it legible? (severity: warning if hard to read)
- Composition issues - is the subject clear? (severity: info)
- Image orientation issues (severity: warning)
- Watermarks or unwanted elements (severity: info)
- Aspect ratio - is it appropriate for social media? (severity: info)

Rules:
- Score 0-100 based on overall quality for social media use
- 90-100: Excellent, ready to post
- 70-89: Good, minor improvements possible
- 50-69: Acceptable but has noticeable issues
- Below 50: Significant quality issues
- Return 2-6 issues, sorted by severity (error > warning > info)
- severity must be one of: ""info"", ""warning"", ""error""
- suggestedFix can be null if no specific fix
- If image is high quality with no issues, return empty issues array with score 95-100
- Output ONLY the JSON, no other text";
    }

    private static string BuildAltTextPrompt()
    {
        return @"You are an accessibility expert. Generate alt text for this image that will help visually impaired users understand the image content.

Return ONLY valid JSON in this exact format:
{
  ""altText"": ""...""
}

Rules:
- Alt text should be 50-150 characters
- Describe the main subject and action in the image
- Include relevant details like colors, setting, and mood if important
- Don't start with ""Image of"" or ""Picture of"" - just describe directly
- Be objective and factual
- If there's text in the image, include it in the description
- Output ONLY the JSON, no other text";
    }

    #endregion

    #region Vision Response Parsers

    private static AiMediaCaptionIdeasResponse ParseImageCaptionResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<VariantsJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse image caption response", 500);

        var variants = parsed.Variants
            .Select(v => new AiMediaCaptionVariant(v.Title, v.Text))
            .ToList();

        return new AiMediaCaptionIdeasResponse(AiMediaAction.CaptionIdeas, variants);
    }

    private static AiImageQualityCheckResponse ParseImageQualityResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<PreFlightJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse image quality response", 500);

        var issues = parsed.Issues
            .Select(i => new AiImageQualityIssue(
                Enum.Parse<AiIssueSeverity>(i.Severity, ignoreCase: true),
                i.Message,
                i.SuggestedFix))
            .ToList();

        return new AiImageQualityCheckResponse(AiMediaAction.ImageQualityCheck, parsed.Score, issues);
    }

    private static AiAltTextResponse ParseAltTextResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<AltTextJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse alt text response", 500);

        return new AiAltTextResponse(AiMediaAction.AltText, parsed.AltText);
    }

    #endregion

    private static AiTextVariantsResponse ParseVariantsResponse(string responseText, AiTextAction action)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<VariantsJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse variants response", 500);

        var variants = parsed.Variants
            .Select(v => new AiTextVariant(v.Title, v.Text))
            .ToList();

        return new AiTextVariantsResponse(action, variants);
    }

    private static AiHashtagsResponse ParseHashtagsResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<HashtagsJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse hashtags response", 500);

        return new AiHashtagsResponse(AiTextAction.Hashtags, parsed.Hashtags);
    }

    private static AiPreFlightResponse ParsePreFlightResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<PreFlightJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse pre-flight response", 500);

        var issues = parsed.Issues
            .Select(i => new AiPreFlightIssue(
                Enum.Parse<AiIssueSeverity>(i.Severity, ignoreCase: true),
                i.Message,
                i.SuggestedFix))
            .ToList();

        return new AiPreFlightResponse(AiTextAction.PreFlight, parsed.Score, issues);
    }

    private static string ExtractJson(string text)
    {
        // Find JSON object in response (in case there's extra text)
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            return text.Substring(start, end - start + 1);
        }

        return text;
    }

    private static string BuildCacheKey(string action, string platform, string tone, string language, string text)
    {
        var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return $"ai:{action}:{platform}:{tone}:{language}:{textHash}";
    }

    #region Gemini API DTOs

    private class GeminiRequest
    {
        public GeminiContent[] Contents { get; set; } = Array.Empty<GeminiContent>();
        public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private class GeminiContent
    {
        public GeminiPart[] Parts { get; set; } = Array.Empty<GeminiPart>();
    }

    private class GeminiPart
    {
        public string Text { get; set; } = string.Empty;
    }

    private class GeminiGenerationConfig
    {
        public double Temperature { get; set; } = 0.7;
        public int MaxOutputTokens { get; set; } = 2048;
        public string? ResponseMimeType { get; set; }
    }

    private class GeminiResponse
    {
        public GeminiCandidate[]? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
    }

    private class VariantsJsonResponse
    {
        public List<VariantItem> Variants { get; set; } = new();
    }

    private class VariantItem
    {
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    private class HashtagsJsonResponse
    {
        public List<string> Hashtags { get; set; } = new();
    }

    private class PreFlightJsonResponse
    {
        public int Score { get; set; }
        public List<PreFlightIssueItem> Issues { get; set; } = new();
    }

    private class PreFlightIssueItem
    {
        public string Severity { get; set; } = "info";
        public string Message { get; set; } = string.Empty;
        public string? SuggestedFix { get; set; }
    }

    private class AltTextJsonResponse
    {
        public string AltText { get; set; } = string.Empty;
    }

    // Vision API request DTOs
    private class GeminiVisionRequest
    {
        public GeminiVisionContent[] Contents { get; set; } = Array.Empty<GeminiVisionContent>();
        public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private class GeminiVisionContent
    {
        public object[] Parts { get; set; } = Array.Empty<object>();
    }

    private class GeminiVisionInlineData
    {
        public GeminiInlineDataContent InlineData { get; set; } = new();
    }

    private class GeminiInlineDataContent
    {
        public string MimeType { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }

    #endregion
}

public class GeminiApiException : Exception
{
    public int StatusCode { get; }

    public GeminiApiException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
