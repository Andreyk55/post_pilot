# AI Assistance Feature

This document describes how to configure and use the AI text assistance feature powered by Google Gemini.

## Configuration

### Environment Variables

Set the following environment variable to enable AI features:

```bash
# Required: Your Google Gemini API key
GEMINI_API_KEY=your-api-key-here

# Optional: Model to use (default: gemini-2.0-flash)
GEMINI_MODEL=gemini-2.0-flash
```

### Getting a Gemini API Key

1. Go to [Google AI Studio](https://aistudio.google.com/app/apikey)
2. Sign in with your Google account
3. Click "Create API key"
4. Copy the generated key
5. Set it as the `GEMINI_API_KEY` environment variable

### Running Locally

#### Backend

```bash
cd backend

# Set the API key (PowerShell)
$env:GEMINI_API_KEY="your-api-key-here"

# Or set it in your shell profile / .env file

# Run the backend
dotnet run
```

#### Frontend

```bash
cd frontend
npm install
npm run dev
```

The frontend will connect to the backend at `http://localhost:5122`.

## API Endpoint

### POST /api/ai/text

Process text with AI assistance.

#### Request Body

```json
{
  "action": "Polish|RewriteTone|Shorten|Expand|Hashtags|PreFlight",
  "platform": "Facebook|Instagram|LinkedIn|X",
  "text": "Your post content here...",
  "tone": "Professional|Casual|Funny|Sales",
  "language": "en"
}
```

**Notes:**
- `tone` is required only for `RewriteTone` action
- `language` defaults to `"en"`
- `text` must be 1-5000 characters

#### Response Examples

**Variants (Polish, RewriteTone, Shorten, Expand):**

```json
{
  "action": "Polish",
  "variants": [
    { "title": "Option 1", "text": "Polished version 1..." },
    { "title": "Option 2", "text": "Polished version 2..." },
    { "title": "Option 3", "text": "Polished version 3..." }
  ]
}
```

**Hashtags:**

```json
{
  "action": "Hashtags",
  "hashtags": ["#social", "#media", "#marketing", "#content"]
}
```

**Pre-flight Check:**

```json
{
  "action": "PreFlight",
  "score": 85,
  "issues": [
    {
      "severity": "warning",
      "message": "Post is quite long for X/Twitter",
      "suggestedFix": "Consider shortening to under 280 characters"
    },
    {
      "severity": "info",
      "message": "No hashtags detected",
      "suggestedFix": "Add 1-3 relevant hashtags"
    }
  ]
}
```

### Example cURL Request

```bash
curl -X POST http://localhost:5122/api/ai/text \
  -H "Content-Type: application/json" \
  -d '{
    "action": "Polish",
    "platform": "Facebook",
    "text": "hey everyone check out our new product its really cool and you should buy it"
  }'
```

## Rate Limiting

- Each user is limited to **20 AI requests per day**
- When the limit is exceeded, the API returns `429 Too Many Requests`
- The limit resets after 24 hours

## Caching

- AI responses are cached for 24 hours
- Cache key is based on: action, platform, tone, language, and text hash
- Repeated identical requests will return cached results instantly

## Supported Actions

| Action | Description | Returns |
|--------|-------------|---------|
| `Polish` | Fix grammar, clarity, remove repetition | 3 variants |
| `RewriteTone` | Rewrite in selected tone | 3 variants |
| `Shorten` | Make text more concise | 3 variants |
| `Expand` | Add detail and CTA | 3 variants |
| `Hashtags` | Suggest relevant hashtags | List of hashtags |
| `PreFlight` | Quality check with score | Score (0-100) + issues |

## Platform-Specific Behavior

- **X (Twitter)**: Variants are limited to 280 characters
- **Facebook/Instagram/LinkedIn**: Variants are limited to 2000 characters
- Hashtag count varies by platform (X: 3, LinkedIn: 5, Instagram: 30, Facebook: 10)

## Error Handling

| Status | Meaning |
|--------|---------|
| `400` | Invalid request (validation errors) |
| `429` | Rate limit exceeded (user or API quota) |
| `503` | AI service unavailable |
| `504` | Request timed out |

## Running Tests

```bash
cd backend.Tests
dotnet test
```
