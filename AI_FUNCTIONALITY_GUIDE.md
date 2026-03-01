# AI Functionality Guide

## What Does the AI Do?

The AI helps you write better social media posts. You type your post, click a button, and get improved versions back.

---

## The 6 AI Buttons

| Button | What It Does | Example |
|--------|--------------|---------|
| **Polish** | Fixes grammar and makes it clearer | "hey check this out" → "Hey, check this out!" |
| **Rewrite** | Changes the tone (Professional/Casual/Funny/Sales) | Casual: "yo check it" → Professional: "Please review this" |
| **Shorten** | Makes it shorter | Long paragraph → One punchy sentence |
| **Expand** | Makes it longer with more details | "New product" → "We're excited to launch our new product! Here's why you'll love it..." |
| **Hashtags** | Suggests hashtags for your post | → #marketing #social #business |
| **Pre-flight** | Checks quality and gives a score (0-100) | Score: 75, Issues: "Missing hashtags", "Too long for Twitter" |

---

## Complete Example: What Happens When You Click "Polish"

Let's trace exactly what happens when you click the Polish button.

### Step 1: You Type Your Post

You're in the app and type:
```
hey everyone check out our new product its really cool and you should buy it
```

You select **Platform: Facebook** and click **✨ Polish**

---

### Step 2: Frontend Sends Request

**File:** `frontend/src/components/AiAssistPanel.tsx`

The frontend sends an HTTP POST to the backend:
```
POST http://localhost:5122/api/ai/text

{
  "action": "Polish",
  "platform": "Facebook",
  "text": "hey everyone check out our new product its really cool and you should buy it",
  "language": "en"
}
```

---

### Step 3: Backend Receives Request

**File:** `backend/Controllers/AiTextController.cs`

The controller:
1. Validates the request (text not empty, not too long)
2. Checks rate limit (max 20 requests/day per user)
3. Calls the AI service

---

### Step 4: Prompt Is Built

**File:** `backend/Services/Ai/GeminiClient.cs` — method `BuildVariantsPrompt`

The system builds a prompt based on:
- **Action** (Polish, Rewrite, Shorten, Expand)
- **Platform** (determines character limit: Twitter=280, others=2000)
- **Tone** (only for Rewrite action)
- **Language** (defaults to "en")

**The final prompt sent to Gemini:**

```
You are a social media content assistant. Polish this text: fix grammar, improve clarity, remove repetition. Keep the original meaning and style.

Platform: Facebook
Language: en
Max characters per variant: 2000

Original text:
hey everyone check out our new product its really cool and you should buy it

Generate exactly 3 distinct variants. Return ONLY valid JSON in this exact format:
{
  "variants": [
    { "title": "Option 1", "text": "..." },
    { "title": "Option 2", "text": "..." },
    { "title": "Option 3", "text": "..." }
  ]
}

Rules:
- Each variant must be under 2000 characters
- Keep variants distinct from each other
- Maintain the core message
- Use appropriate style for Facebook
- Output ONLY the JSON, no other text
```

---

### Step 5: Call Gemini API

**File:** `backend/Services/Ai/GeminiClient.cs` — method `CallGeminiAsync`

The request is sent to Google Gemini with these settings:

| Setting | Value | Meaning |
|---------|-------|---------|
| **Temperature** | 0.7 | How creative (0=boring, 1=wild). 0.7 = creative but coherent |
| **MaxOutputTokens** | 2048 | Max response length (~8000 characters) |
| **ResponseMimeType** | application/json | Forces clean JSON output |

**What gets sent to Google Gemini:**
```json
{
  "contents": [
    {
      "parts": [
        { "text": "You are a social media content assistant. Polish this text..." }
      ]
    }
  ],
  "generationConfig": {
    "temperature": 0.7,
    "maxOutputTokens": 2048,
    "responseMimeType": "application/json"
  }
}
```

---

### Step 6: Gemini Returns Response

Google Gemini responds with:
```json
{
  "candidates": [
    {
      "content": {
        "parts": [
          {
            "text": "{\"variants\": [{\"title\": \"Option 1\", \"text\": \"Hey everyone! Check out our amazing new product—it's really cool, and you're going to love it!\"}, ...]}"
          }
        ]
      }
    }
  ]
}
```

---

### Step 7: Parse and Return

**File:** `backend/Services/Ai/GeminiClient.cs`

The response is parsed and returned to the frontend:
```json
{
  "action": "Polish",
  "variants": [
    { "title": "Option 1", "text": "Hey everyone! Check out our amazing new product—it's really cool, and you're going to love it!" },
    { "title": "Option 2", "text": "Excited to share our new product with you all. It's fantastic—don't miss out!" },
    { "title": "Option 3", "text": "Our new product just launched! It's awesome, and we think you should give it a try." }
  ]
}
```

---

### Step 8: User Sees Results

**File:** `frontend/src/components/AiAssistPanel.tsx`

The UI shows 3 cards with:
- The polished text
- Character count
- **Apply** button (replaces your post)
- **Copy** button (copies to clipboard)

---

## How Each Action Changes the Prompt

The key difference is the instruction sent to the AI:

| Action | Instruction Sent to AI |
|--------|------------------------|
| **Polish** | "Polish this text: fix grammar, improve clarity, remove repetition. Keep the original meaning and style." |
| **Rewrite (Professional)** | "Rewrite this text in a professional tone." |
| **Rewrite (Casual)** | "Rewrite this text in a casual tone." |
| **Rewrite (Funny)** | "Rewrite this text in a funny tone." |
| **Rewrite (Sales)** | "Rewrite this text in a sales tone." |
| **Shorten** | "Shorten this text while keeping the key message. Be concise." |
| **Expand** | "Expand this text with more detail, examples, and a call-to-action." |

---

## How Platform Changes the Prompt

Character limits by platform:

| Platform | Max Characters |
|----------|----------------|
| X (Twitter) | 280 |
| Facebook | 2000 |
| Instagram | 2000 |
| LinkedIn | 2000 |

The prompt includes: `Max characters per variant: {maxLength}`

So for Twitter, the AI knows to keep responses under 280 chars.

---

## Hashtags Prompt

**File:** `backend/Services/Ai/GeminiClient.cs` — method `BuildHashtagsPrompt`

Hashtag limits by platform:

| Platform | Max Hashtags |
|----------|--------------|
| Instagram | 30 |
| Facebook | 10 |
| LinkedIn | 5 |
| X (Twitter) | 3 |

**Example prompt for Instagram:**

```
You are a social media hashtag expert. Suggest relevant hashtags for this post.

Platform: Instagram
Language: en
Max hashtags: 30

Post text:
Just finished a great workout at the gym!

Return ONLY valid JSON in this exact format:
{
  "hashtags": ["#tag1", "#tag2", "#tag3"]
}

Rules:
- Suggest 5-30 relevant hashtags
- Include mix of popular and niche hashtags
- All hashtags must start with #
- Use en language hashtags primarily
- Consider Instagram best practices
- Output ONLY the JSON, no other text
```

---

## Pre-flight Prompt (Quality Check)

**File:** `backend/Services/Ai/GeminiClient.cs` — method `BuildPreFlightPrompt`

**Example prompt:**

```
You are a social media content reviewer. Analyze this post and provide a quality score and issues.

Platform: X
Language: en
Character limit: 280
Current length: 42 characters

Post text:
CHECK OUT OUR NEW PRODUCT NOW!!! BUY BUY BUY

Return ONLY valid JSON in this exact format:
{
  "score": 85,
  "issues": [
    { "severity": "warning", "message": "...", "suggestedFix": "..." },
    { "severity": "info", "message": "...", "suggestedFix": null }
  ]
}

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
- severity must be one of: "info", "warning", "error"
- suggestedFix can be null if no specific fix
- Output ONLY the JSON, no other text
```

---

## What is Temperature and MaxOutputTokens?

### Temperature = 0.7

Controls how "creative" vs "predictable" the AI is:

| Value | Behavior |
|-------|----------|
| 0.0 | Always same output. Boring but consistent |
| 0.5 | Mostly predictable with some variation |
| **0.7** | Creative but coherent. Good for writing |
| 1.0+ | Very random, might be weird |

**Why 0.7?** We want 3 different variants, so we need some creativity. But not so much that the output is nonsense.

### MaxOutputTokens = 2048

**Tokens** = pieces of words. Roughly:
- 1 token ≈ 4 characters
- 2048 tokens ≈ 8,000 characters

This limits how long the AI's response can be. 2048 is plenty for 3 variants.

### ResponseMimeType = "application/json"

Forces Gemini to return valid JSON. Without this, it might add extra text like "Here are your variants:".

---

## File Summary

| File | What It Does |
|------|--------------|
| `frontend/src/components/AiAssistPanel.tsx` | UI buttons, shows results |
| `frontend/src/api/ai.ts` | Sends HTTP requests to backend |
| `backend/Controllers/AiTextController.cs` | Receives requests, validates, rate limits |
| `backend/Services/Ai/GeminiClient.cs` | **Builds prompts**, calls Gemini API, parses response |
| `backend/Services/Ai/GeminiSettings.cs` | Stores API key, model name, timeout |
| `backend/DTOs/AiTextDTOs.cs` | Defines request/response types |

---

## Configuration

Set these environment variables:

```bash
Gemini__ApiKey=your-api-key-here    # Required (env var)
# Model defaults to gemini-2.0-flash (set in config/appsettings.common.json)
```

Get an API key at: https://aistudio.google.com/app/apikey
