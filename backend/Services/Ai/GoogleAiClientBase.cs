using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Base class for Google AI clients (Gemini and Gemma).
/// Contains shared HTTP logic, caching, prompt building, and response parsing.
/// </summary>
public abstract class GoogleAiClientBase
{
    protected readonly HttpClient HttpClient;
    protected readonly GeminiSettings Settings;
    protected readonly IMemoryCache Cache;
    protected readonly ILogger Logger;

    protected static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    // Platform character limits
    protected const int CharLimitX = 280;
    protected const int CharLimitDefault = 2000;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    protected GoogleAiClientBase(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILogger logger)
    {
        HttpClient = httpClient;
        Settings = settings;
        Cache = cache;
        Logger = logger;

        HttpClient.Timeout = TimeSpan.FromSeconds(Settings.TimeoutSeconds);
    }

    /// <summary>
    /// Whether this client supports JSON mode (ResponseMimeType = "application/json").
    /// Gemini supports it, Gemma does not.
    /// </summary>
    protected abstract bool SupportsJsonMode { get; }

    /// <summary>
    /// Whether this client supports vision (image) inputs.
    /// </summary>
    protected abstract bool SupportsVision { get; }

    /// <summary>
    /// Client name for logging purposes (e.g., "Gemini", "Gemma").
    /// </summary>
    protected abstract string ClientName { get; }

    #region HTTP Call Methods

    protected async Task<string> CallGenerateContentAsync(
        string prompt,
        CancellationToken cancellationToken,
        int maxOutputTokens = 2048)
    {
        var url = BuildApiUrl();
        var urlForLogging = BuildApiUrlForLogging();

        var request = new GeminiRequest
        {
            Contents = new[]
            {
                new GeminiContent
                {
                    Parts = new[] { new GeminiPart { Text = prompt } }
                }
            },
            GenerationConfig = BuildGenerationConfig(maxOutputTokens)
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        return await ExecuteRequestAsync(url, urlForLogging, content, cancellationToken);
    }

    protected async Task<string> CallVisionAsync(
        string prompt,
        byte[] imageBytes,
        string imageMimeType,
        int maxOutputTokens = 512,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsVision)
        {
            throw new GeminiApiException(
                $"{ClientName} model '{Settings.Model}' does not support vision. Use a Gemini model for image processing.",
                400);
        }

        var url = BuildApiUrl();
        var urlForLogging = BuildApiUrlForLogging();

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
            GenerationConfig = BuildGenerationConfig(maxOutputTokens)
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        return await ExecuteRequestAsync(url, urlForLogging, content, cancellationToken, isVision: true);
    }

    private async Task<string> ExecuteRequestAsync(
        string url,
        string urlForLogging,
        StringContent content,
        CancellationToken cancellationToken,
        bool isVision = false)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var apiType = isVision ? "Vision" : "Text";

        try
        {
            var response = await HttpClient.PostAsync(url, content, cancellationToken);
            stopwatch.Stop();

            // Always read response body for better error diagnostics
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            Logger.LogInformation(
                "{Client} {ApiType} API call to {Model} completed in {ElapsedMs}ms, Status: {StatusCode}",
                ClientName, apiType, Settings.Model, stopwatch.ElapsedMilliseconds, (int)response.StatusCode);

            // Handle specific error codes with detailed logging
            if (!response.IsSuccessStatusCode)
            {
                LogErrorResponse(response.StatusCode, responseBody, urlForLogging);
                ThrowForStatusCode(response.StatusCode, responseBody, isVision);
            }

            var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonOptions);
            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.LogWarning("{Client} API returned empty response. Body: {Body}", ClientName, responseBody);
                throw new GeminiApiException($"Empty response from {ClientName} API", 500);
            }

            return text;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogError("{Client} {ApiType} API request to {Model} timed out after {ElapsedMs}ms",
                ClientName, apiType, Settings.Model, stopwatch.ElapsedMilliseconds);
            throw new GeminiApiException("Request timed out", 504);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP error calling {Client} {ApiType} API for model {Model}",
                ClientName, apiType, Settings.Model);
            throw new GeminiApiException($"Failed to connect to AI service: {ex.Message}", 503);
        }
    }

    private void LogErrorResponse(HttpStatusCode statusCode, string responseBody, string urlForLogging)
    {
        Logger.LogError(
            "{Client} API error. Model: {Model}, URL: {Url}, Status: {StatusCode}, Body: {Body}",
            ClientName, Settings.Model, urlForLogging, (int)statusCode, responseBody);
    }

    private void ThrowForStatusCode(HttpStatusCode statusCode, string responseBody, bool isVision)
    {
        switch (statusCode)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new GeminiApiException("API key is invalid or misconfigured", (int)statusCode);

            case HttpStatusCode.TooManyRequests:
                throw new GeminiApiException("Rate limit exceeded. Please try again later.", 429);

            case HttpStatusCode.BadRequest:
                var errorMessage = ParseErrorMessage(responseBody);

                if (isVision && (responseBody.Contains("size") || responseBody.Contains("large")))
                {
                    throw new GeminiApiException("Image too large for AI processing", 413);
                }

                if (responseBody.Contains("FAILED_PRECONDITION"))
                {
                    throw new GeminiApiException($"Model precondition failed: {errorMessage}", 400);
                }

                if (responseBody.Contains("INVALID_ARGUMENT"))
                {
                    throw new GeminiApiException($"Invalid request: {errorMessage}", 400);
                }

                throw new GeminiApiException(isVision ? "Invalid image or request format" : $"Bad request: {errorMessage}", 400);

            default:
                throw new GeminiApiException($"API error: {statusCode}", (int)statusCode);
        }
    }

    private static string ParseErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "Unknown error";
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return "Unknown error";
    }

    private string BuildApiUrl()
    {
        return $"{Settings.BaseUrl}/models/{Settings.Model}:generateContent?key={Settings.ApiKey}";
    }

    private string BuildApiUrlForLogging()
    {
        return $"{Settings.BaseUrl}/models/{Settings.Model}:generateContent";
    }

    private GeminiGenerationConfig BuildGenerationConfig(int maxOutputTokens)
    {
        return new GeminiGenerationConfig
        {
            Temperature = 0.7,
            MaxOutputTokens = maxOutputTokens,
            ResponseMimeType = SupportsJsonMode ? "application/json" : null
        };
    }

    #endregion

    #region Prompt Builders

    protected static string BuildVariantsPrompt(AiTextAction action, AiPlatform platform, string text, AiTone? tone, string language)
    {
        var maxLength = platform == AiPlatform.X ? CharLimitX : CharLimitDefault;
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

    protected static string BuildHashtagsPrompt(AiPlatform platform, string text, string language)
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
Target Language: {language}
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
- IMPORTANT: Generate hashtags in {language} language (e.g., if {language}=he, use Hebrew words like #מבצע, #חדש)
- Do NOT translate to English unless the target language is 'en'
- No spaces in hashtags (use underscores if needed)
- Avoid punctuation and emojis inside hashtags
- Keep tags short and relevant
- Consider {platform} best practices
- Output ONLY the JSON, no other text";
    }

    protected static string BuildPreFlightPrompt(AiPlatform platform, string text, string language)
    {
        var charLimit = platform == AiPlatform.X ? CharLimitX : CharLimitDefault;

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
- Return 3-6 issues maximum, sorted by severity (error > warning > info)
- severity must be one of: ""info"", ""warning"", ""error""
- Keep messages under 80 characters
- Keep suggestedFix under 100 characters, or use null if no specific fix
- Output ONLY the JSON, no other text";
    }

    protected static string BuildCreatorVariantsPrompt(AiGenerateVariantsRequest request, int numVariants)
    {
        var maxLength = request.Platform == AiPlatform.X ? CharLimitX : CharLimitDefault;
        var toneStr = request.Tone.ToString().ToLower();

        var lengthGuidance = request.Length switch
        {
            AiLength.Short => "1-2 sentences (under 100 characters ideal, max 150)",
            AiLength.Medium => "3-5 sentences (150-300 characters)",
            AiLength.Long => "6-10 sentences (300-600 characters, or more for longer platforms)",
            _ => "3-5 sentences"
        };

        var goalInstruction = request.Goal switch
        {
            AiGoal.Engage => "Create engaging content that encourages interaction, comments, and shares. Ask questions or spark discussion.",
            AiGoal.Promote => "Create promotional content that highlights value, benefits, and drives action. Focus on offers or unique selling points.",
            AiGoal.Announce => "Create announcement-style content that clearly communicates news, updates, or important information.",
            AiGoal.Educate => "Create educational content that provides tips, insights, or valuable information. Position as helpful and informative.",
            AiGoal.Story => "Create narrative-style content that tells a mini story or shares an experience. Use a personal, relatable voice.",
            _ => "Create engaging social media content."
        };

        var includeInstructions = new List<string>();
        if (request.IncludeEmojis)
            includeInstructions.Add("Include relevant emojis naturally throughout the text");
        else
            includeInstructions.Add("Do NOT include any emojis");

        if (request.IncludeHashtags)
            includeInstructions.Add("Include 2-5 relevant hashtags at the end");
        else
            includeInstructions.Add("Do NOT include any hashtags");

        if (request.IncludeCta)
            includeInstructions.Add("Include a clear call-to-action (e.g., 'Learn more', 'Shop now', 'Comment below', 'Link in bio')");

        if (request.IncludeQuestion)
            includeInstructions.Add("End with an engaging question to encourage comments");

        var includeSection = string.Join("\n- ", includeInstructions);

        return $@"You are a social media content creator assistant. Generate {numVariants} distinct text variant(s) based on the following input and requirements.

INPUT TEXT:
{request.InputText}

REQUIREMENTS:
- Platform: {request.Platform}
- Goal: {request.Goal} - {goalInstruction}
- Tone: {toneStr}
- Length: {request.Length} - {lengthGuidance}
- Language: {request.Language}
- Max characters: {maxLength}

INCLUDE/EXCLUDE:
- {includeSection}

Return ONLY valid JSON in this exact format:
{{
  ""variants"": [
    {{ ""id"": ""v1"", ""text"": ""..."" }},
    {{ ""id"": ""v2"", ""text"": ""..."" }},
    {{ ""id"": ""v3"", ""text"": ""..."" }}
  ]
}}

RULES:
- Generate exactly {numVariants} variant(s)
- Each variant must be unique and distinct in approach/wording
- Each variant must respect the max character limit ({maxLength})
- Maintain the core message from the input
- Apply the {toneStr} tone consistently
- Follow the {request.Goal} goal structure
- DO NOT include labels like ""Option 1:"" or ""Variant 1:"" in the text itself
- Output plain text only (no markdown formatting)
- Output ONLY the JSON, no other text";
    }

    protected static string BuildImageCaptionPrompt(AiPlatform platform, string? existingText, string language)
    {
        var maxLength = platform == AiPlatform.X ? CharLimitX : CharLimitDefault;
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

    protected static string BuildImageQualityPrompt()
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

    protected static string BuildAltTextPrompt()
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

    #region Voice Profile-Aware Prompt Builders

    /// <summary>
    /// Builds the voice profile section for prompts.
    /// </summary>
    protected static string BuildVoiceProfileSection(AiVoiceProfile? profile)
    {
        if (profile == null)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("\n=== BRAND VOICE PROFILE ===");

        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            sb.AppendLine($"Brand/Audience: {profile.Description}");
        }

        if (!string.IsNullOrWhiteSpace(profile.DoRules))
        {
            sb.AppendLine("\nDO (style guidelines):");
            foreach (var rule in profile.DoRules.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.AppendLine($"- {rule.Trim()}");
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.DontRules))
        {
            sb.AppendLine("\nDON'T (avoid these):");
            foreach (var rule in profile.DontRules.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.AppendLine($"- {rule.Trim()}");
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.BannedWords))
        {
            var banned = profile.BannedWords
                .Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim())
                .Where(w => !string.IsNullOrWhiteSpace(w));
            sb.AppendLine($"\nBANNED WORDS/PHRASES (never use): {string.Join(", ", banned)}");
        }

        if (!string.IsNullOrWhiteSpace(profile.ExamplePosts))
        {
            sb.AppendLine("\nEXAMPLE POSTS (match this style):");
            var examples = profile.ExamplePosts.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < examples.Length && i < 3; i++)
            {
                sb.AppendLine($"Example {i + 1}: \"{examples[i].Trim()}\"");
            }
        }

        sb.AppendLine("=== END VOICE PROFILE ===\n");
        return sb.ToString();
    }

    protected static string BuildVariantsPromptWithVoice(AiTextAction action, AiPlatform platform, string text, AiTone? tone, string language, AiVoiceProfile? profile)
    {
        var basePrompt = BuildVariantsPrompt(action, platform, text, tone, language);
        if (profile == null)
            return basePrompt;

        var voiceSection = BuildVoiceProfileSection(profile);

        // Insert voice section after the first line of instructions, with priority rule
        var priorityRule = "\n\nPRIORITY RULE: Follow the Voice Profile strictly. Apply Tone only as a light adjustment within the Voice Profile. If Tone conflicts with Voice rules, follow Voice.\n";
        var insertPoint = basePrompt.IndexOf("\n\nPlatform:");
        if (insertPoint > 0)
        {
            return basePrompt.Insert(insertPoint, priorityRule + voiceSection);
        }

        return priorityRule + voiceSection + basePrompt;
    }

    protected static string BuildHashtagsPromptWithVoice(AiPlatform platform, string text, string language, AiVoiceProfile? profile)
    {
        var basePrompt = BuildHashtagsPrompt(platform, text, language);
        if (profile == null)
            return basePrompt;

        // Always include voice section when profile exists, with priority rule
        var voiceSection = BuildVoiceProfileSection(profile);
        var priorityRule = "\n\nPRIORITY RULE: Use the Voice Profile to infer niche/audience and avoid banned terms. IMPORTANT: Do NOT include any banned words/phrases from the Voice Profile (including inside hashtags).\n";

        var insertPoint = basePrompt.IndexOf("\n\nPost text:");
        if (insertPoint > 0)
        {
            return basePrompt.Insert(insertPoint, priorityRule + voiceSection);
        }

        return priorityRule + voiceSection + basePrompt;
    }

    protected static string BuildPreFlightPromptWithVoice(AiPlatform platform, string text, string language, AiVoiceProfile? profile)
    {
        var charLimit = platform == AiPlatform.X ? CharLimitX : CharLimitDefault;

        var voiceSection = "";
        var bannedWordsCheck = "";
        var priorityRule = "";

        if (profile != null)
        {
            voiceSection = BuildVoiceProfileSection(profile);
            priorityRule = "\n\nPRIORITY RULE: When evaluating content quality, Voice Profile rules take precedence over general best practices. Flag violations of Voice Profile rules as errors.\n";

            if (!string.IsNullOrWhiteSpace(profile.BannedWords))
            {
                var banned = profile.BannedWords
                    .Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim().ToLowerInvariant())
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .ToList();

                if (banned.Count > 0)
                {
                    bannedWordsCheck = $"\n- Banned words used (severity: error) - check for: {string.Join(", ", banned)}";
                }
            }
        }

        return $@"You are a social media content reviewer. Analyze this post and provide a quality score and issues.
{priorityRule}{voiceSection}
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
- Character limit violations (severity: error){bannedWordsCheck}
- Missing call-to-action (severity: info)
- Readability issues (severity: warning)
- Engagement optimization (severity: info)
- Platform-specific best practices (severity: info)
- Overuse of caps or punctuation (severity: warning)
- Missing hashtags if appropriate (severity: info)
{(profile != null ? "- Voice profile rule violations (severity: error)" : "")}

Rules:
- Score 0-100 based on overall quality
- Return 3-6 issues maximum, sorted by severity (error > warning > info)
- severity must be one of: ""info"", ""warning"", ""error""
- Keep messages under 80 characters
- Keep suggestedFix under 100 characters, or use null if no specific fix
{(profile != null && !string.IsNullOrWhiteSpace(profile.BannedWords) ? "- If banned words are found, flag as error and suggest alternatives" : "")}
- Output ONLY the JSON, no other text";
    }

    protected static string BuildCreatorVariantsPromptWithVoice(AiGenerateVariantsRequest request, int numVariants, AiVoiceProfile? profile)
    {
        var maxLength = request.Platform == AiPlatform.X ? CharLimitX : CharLimitDefault;
        var toneStr = request.Tone.ToString().ToLower();

        var lengthGuidance = request.Length switch
        {
            AiLength.Short => "1-2 sentences (under 100 characters ideal, max 150)",
            AiLength.Medium => "3-5 sentences (150-300 characters)",
            AiLength.Long => "6-10 sentences (300-600 characters, or more for longer platforms)",
            _ => "3-5 sentences"
        };

        var goalInstruction = request.Goal switch
        {
            AiGoal.Engage => "Create engaging content that encourages interaction, comments, and shares. Ask questions or spark discussion.",
            AiGoal.Promote => "Create promotional content that highlights value, benefits, and drives action. Focus on offers or unique selling points.",
            AiGoal.Announce => "Create announcement-style content that clearly communicates news, updates, or important information.",
            AiGoal.Educate => "Create educational content that provides tips, insights, or valuable information. Position as helpful and informative.",
            AiGoal.Story => "Create narrative-style content that tells a mini story or shares an experience. Use a personal, relatable voice.",
            _ => "Create engaging social media content."
        };

        var includeInstructions = new List<string>();
        if (request.IncludeEmojis)
            includeInstructions.Add("Include relevant emojis naturally throughout the text");
        else
            includeInstructions.Add("Do NOT include any emojis");

        if (request.IncludeHashtags)
            includeInstructions.Add("Include 2-5 relevant hashtags at the end");
        else
            includeInstructions.Add("Do NOT include any hashtags");

        if (request.IncludeCta)
            includeInstructions.Add("Include a clear call-to-action (e.g., 'Learn more', 'Shop now', 'Comment below', 'Link in bio')");

        if (request.IncludeQuestion)
            includeInstructions.Add("End with an engaging question to encourage comments");

        var includeSection = string.Join("\n- ", includeInstructions);

        // Build dynamic JSON example based on numVariants
        var jsonExampleVariants = new List<string>();
        for (int i = 1; i <= numVariants; i++)
        {
            jsonExampleVariants.Add($@"    {{ ""id"": ""v{i}"", ""text"": ""..."" }}");
        }
        var jsonExample = string.Join(",\n", jsonExampleVariants);

        var voiceSection = BuildVoiceProfileSection(profile);
        var voiceRulesReminder = profile != null
            ? "\n- CRITICAL: Respect all voice profile rules, especially banned words\n- Match the style shown in example posts\n- PRIORITY RULE: Follow the Voice Profile strictly. Apply Tone only as a light adjustment within the Voice Profile. If Tone conflicts with Voice rules, follow Voice."
            : "";

        return $@"You are a social media content creator assistant. Generate {numVariants} distinct text variant(s) based on the following input and requirements.
{voiceSection}
INPUT TEXT:
{request.InputText}

REQUIREMENTS:
- Platform: {request.Platform}
- Goal: {request.Goal} - {goalInstruction}
- Tone: {toneStr}
- Length: {request.Length} - {lengthGuidance}
- Language: {request.Language}
- Max characters: {maxLength}

INCLUDE/EXCLUDE:
- {includeSection}

Return ONLY valid JSON in this exact format:
{{
  ""variants"": [
{jsonExample}
  ]
}}

RULES:
- Generate exactly {numVariants} variant(s)
- Each variant must be unique and distinct in approach/wording
- Each variant must respect the max character limit ({maxLength})
- Maintain the core message from the input
- Apply tone as a modifier within the Voice Profile boundaries (Voice Profile overrides Tone if conflict)
- Follow the {request.Goal} goal structure{voiceRulesReminder}
- DO NOT include labels like ""Option 1:"" or ""Variant 1:"" in the text itself
- Output plain text only (no markdown formatting)
- Output ONLY the JSON, no other text";
    }

    #endregion

    #region Cache Key Helpers with Voice Profile

    protected static string BuildCacheKeyWithVoiceProfile(string action, string platform, string tone, string language, string text, AiVoiceProfile? profile)
    {
        var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        var voiceKey = profile != null ? $":{profile.Id}:{profile.UpdatedAt.Ticks}" : "";
        return $"ai:{action}:{platform}:{tone}:{language}:{textHash}{voiceKey}";
    }

    protected static string BuildCreatorVariantsCacheKeyWithVoice(AiGenerateVariantsRequest request, AiVoiceProfile? profile)
    {
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.InputText)))[..16];
        var flags = $"{(request.IncludeEmojis ? "E" : "")}{(request.IncludeHashtags ? "H" : "")}{(request.IncludeCta ? "C" : "")}{(request.IncludeQuestion ? "Q" : "")}";
        var voiceKey = profile != null ? $":{profile.Id}:{profile.UpdatedAt.Ticks}" : "";
        return $"ai:CreatorVariants:{request.Platform}:{request.Goal}:{request.Tone}:{request.Length}:{flags}:{request.Language}:{inputHash}{voiceKey}";
    }

    #endregion

    #region Response Parsers

    protected static AiTextVariantsResponse ParseVariantsResponse(string responseText, AiTextAction action)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<VariantsJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse variants response", 500);

        var variants = parsed.Variants
            .Select(v => new AiTextVariant(v.Title, v.Text))
            .ToList();

        return new AiTextVariantsResponse(action, variants);
    }

    protected static AiHashtagsResponse ParseHashtagsResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<HashtagsJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse hashtags response", 500);

        return new AiHashtagsResponse(AiTextAction.Hashtags, parsed.Hashtags);
    }

    protected static AiPreFlightResponse ParsePreFlightResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        PreFlightJsonResponse? parsed = null;

        try
        {
            parsed = JsonSerializer.Deserialize<PreFlightJsonResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            parsed = TrySalvagePartialPreFlightJson(json);
        }

        if (parsed == null)
        {
            throw new GeminiApiException("Failed to parse pre-flight response", 500);
        }

        var issues = parsed.Issues
            .Select(i => new AiPreFlightIssue(
                Enum.Parse<AiIssueSeverity>(i.Severity, ignoreCase: true),
                i.Message,
                i.SuggestedFix))
            .ToList();

        return new AiPreFlightResponse(AiTextAction.PreFlight, parsed.Score, issues);
    }

    protected static AiGenerateVariantsResponse ParseCreatorVariantsResponse(string responseText, int expectedCount)
    {
        var json = ExtractJson(responseText);

        CreatorVariantsJsonResponse? parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize<CreatorVariantsJsonResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            parsed = TrySalvagePartialVariantsJson(json);
        }

        if (parsed == null || parsed.Variants.Count == 0)
        {
            throw new GeminiApiException("Failed to parse creator variants response. The AI response may have been truncated.", 500);
        }

        var variants = parsed.Variants
            .Where(v => !string.IsNullOrWhiteSpace(v.Text))
            .Select(v => new AiGeneratedVariant(
                string.IsNullOrWhiteSpace(v.Id) ? Guid.NewGuid().ToString("N")[..8] : v.Id,
                v.Text.Trim()))
            .ToList();

        if (variants.Count == 0)
        {
            throw new GeminiApiException("No valid variants in response", 500);
        }

        return new AiGenerateVariantsResponse(variants);
    }

    protected static AiMediaCaptionIdeasResponse ParseImageCaptionResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<VariantsJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse image caption response", 500);

        var variants = parsed.Variants
            .Select(v => new AiMediaCaptionVariant(v.Title, v.Text))
            .ToList();

        return new AiMediaCaptionIdeasResponse(AiMediaAction.CaptionIdeas, variants);
    }

    protected static AiImageQualityCheckResponse ParseImageQualityResponse(string responseText)
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

    protected static AiAltTextResponse ParseAltTextResponse(string responseText)
    {
        var json = ExtractJson(responseText);
        var parsed = JsonSerializer.Deserialize<AltTextJsonResponse>(json, JsonOptions)
            ?? throw new GeminiApiException("Failed to parse alt text response", 500);

        return new AiAltTextResponse(AiMediaAction.AltText, parsed.AltText);
    }

    protected static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            return text.Substring(start, end - start + 1);
        }

        return text;
    }

    #endregion

    #region Salvage Methods for Truncated JSON

    protected static CreatorVariantsJsonResponse? TrySalvagePartialVariantsJson(string json)
    {
        try
        {
            var variants = new List<CreatorVariantItem>();
            var currentPos = 0;

            var variantsStart = json.IndexOf("\"variants\"", StringComparison.OrdinalIgnoreCase);
            if (variantsStart < 0) return null;

            var arrayStart = json.IndexOf('[', variantsStart);
            if (arrayStart < 0) return null;

            currentPos = arrayStart + 1;

            while (currentPos < json.Length)
            {
                var objectStart = json.IndexOf('{', currentPos);
                if (objectStart < 0) break;

                var objectEnd = FindMatchingBrace(json, objectStart);
                if (objectEnd < 0) break;

                var objectJson = json.Substring(objectStart, objectEnd - objectStart + 1);
                try
                {
                    var variant = JsonSerializer.Deserialize<CreatorVariantItem>(objectJson, JsonOptions);
                    if (variant != null && !string.IsNullOrWhiteSpace(variant.Text))
                    {
                        variants.Add(variant);
                    }
                }
                catch
                {
                    // Skip unparseable object
                }

                currentPos = objectEnd + 1;
            }

            if (variants.Count > 0)
            {
                return new CreatorVariantsJsonResponse { Variants = variants };
            }
        }
        catch
        {
            // If salvage fails, return null
        }

        return null;
    }

    protected static PreFlightJsonResponse? TrySalvagePartialPreFlightJson(string json)
    {
        try
        {
            var issues = new List<PreFlightIssueItem>();
            var score = 0;

            var scoreMatch = System.Text.RegularExpressions.Regex.Match(json, @"""score""\s*:\s*(\d+)");
            if (scoreMatch.Success)
            {
                score = int.Parse(scoreMatch.Groups[1].Value);
            }

            var issuesStart = json.IndexOf("\"issues\"", StringComparison.OrdinalIgnoreCase);
            if (issuesStart < 0)
            {
                if (scoreMatch.Success)
                {
                    return new PreFlightJsonResponse { Score = score, Issues = issues };
                }
                return null;
            }

            var arrayStart = json.IndexOf('[', issuesStart);
            if (arrayStart < 0) return null;

            var currentPos = arrayStart + 1;

            while (currentPos < json.Length)
            {
                var objectStart = json.IndexOf('{', currentPos);
                if (objectStart < 0) break;

                var objectEnd = FindMatchingBrace(json, objectStart);
                if (objectEnd < 0) break;

                var objectJson = json.Substring(objectStart, objectEnd - objectStart + 1);
                try
                {
                    var issue = JsonSerializer.Deserialize<PreFlightIssueItem>(objectJson, JsonOptions);
                    if (issue != null && !string.IsNullOrWhiteSpace(issue.Message))
                    {
                        issues.Add(issue);
                    }
                }
                catch
                {
                    // Skip unparseable object
                }

                currentPos = objectEnd + 1;
            }

            if (scoreMatch.Success || issues.Count > 0)
            {
                return new PreFlightJsonResponse { Score = score, Issues = issues };
            }
        }
        catch
        {
            // If salvage fails, return null
        }

        return null;
    }

    private static int FindMatchingBrace(string json, int openBracePos)
    {
        var braceCount = 1;
        var inString = false;
        var escapeNext = false;

        for (var i = openBracePos + 1; i < json.Length; i++)
        {
            var c = json[i];

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escapeNext = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (c == '{') braceCount++;
                else if (c == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        return i;
                    }
                }
            }
        }

        return -1; // No matching brace found
    }

    #endregion

    #region Cache Key Helpers

    protected static string BuildCacheKey(string action, string platform, string tone, string language, string text)
    {
        var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return $"ai:{action}:{platform}:{tone}:{language}:{textHash}";
    }

    protected static string BuildCreatorVariantsCacheKey(AiGenerateVariantsRequest request)
    {
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.InputText)))[..16];
        var flags = $"{(request.IncludeEmojis ? "E" : "")}{(request.IncludeHashtags ? "H" : "")}{(request.IncludeCta ? "C" : "")}{(request.IncludeQuestion ? "Q" : "")}";
        return $"ai:CreatorVariants:{request.Platform}:{request.Goal}:{request.Tone}:{request.Length}:{flags}:{request.Language}:{inputHash}";
    }

    #endregion

    #region Gemini API DTOs

    protected class GeminiRequest
    {
        public GeminiContent[] Contents { get; set; } = Array.Empty<GeminiContent>();
        public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    protected class GeminiContent
    {
        public GeminiPart[] Parts { get; set; } = Array.Empty<GeminiPart>();
    }

    protected class GeminiPart
    {
        public string Text { get; set; } = string.Empty;
    }

    protected class GeminiGenerationConfig
    {
        public double Temperature { get; set; } = 0.7;
        public int MaxOutputTokens { get; set; } = 2048;
        public string? ResponseMimeType { get; set; }
    }

    protected class GeminiResponse
    {
        public GeminiCandidate[]? Candidates { get; set; }
    }

    protected class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
    }

    protected class VariantsJsonResponse
    {
        public List<VariantItem> Variants { get; set; } = new();
    }

    protected class VariantItem
    {
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    protected class HashtagsJsonResponse
    {
        public List<string> Hashtags { get; set; } = new();
    }

    protected class PreFlightJsonResponse
    {
        public int Score { get; set; }
        public List<PreFlightIssueItem> Issues { get; set; } = new();
    }

    protected class PreFlightIssueItem
    {
        public string Severity { get; set; } = "info";
        public string Message { get; set; } = string.Empty;
        public string? SuggestedFix { get; set; }
    }

    protected class AltTextJsonResponse
    {
        public string AltText { get; set; } = string.Empty;
    }

    protected class CreatorVariantsJsonResponse
    {
        public List<CreatorVariantItem> Variants { get; set; } = new();
    }

    protected class CreatorVariantItem
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    protected class GeminiVisionRequest
    {
        public GeminiVisionContent[] Contents { get; set; } = Array.Empty<GeminiVisionContent>();
        public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    protected class GeminiVisionContent
    {
        public object[] Parts { get; set; } = Array.Empty<object>();
    }

    protected class GeminiVisionInlineData
    {
        public GeminiInlineDataContent InlineData { get; set; } = new();
    }

    protected class GeminiInlineDataContent
    {
        public string MimeType { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }

    #endregion
}
