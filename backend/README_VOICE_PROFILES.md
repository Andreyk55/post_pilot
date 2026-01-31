# Brand Voice Profiles

Brand Voice Profiles allow users to define consistent style guidelines that influence all AI text operations.

## Overview

Voice profiles define:
- **Brand/Audience Description**: Who you're speaking to and your brand personality
- **Do Rules**: Style guidelines the AI should follow (e.g., "Use active voice", "Include statistics")
- **Don't Rules**: Things to avoid (e.g., "Don't use jargon", "Avoid passive voice")
- **Banned Words**: Words/phrases that must never appear in output
- **Example Posts**: Sample posts that demonstrate the desired voice/style

## How It Works

1. **Profile Selection**: In the AI Assist panel, select a voice profile from the dropdown (or "None" for default behavior)
2. **Create/Edit**: Click "+ Create new..." to create a profile, or "Edit" to modify the selected profile
3. **AI Integration**: When a profile is selected, it influences ALL AI text operations:
   - Generate Variants
   - Quick actions (Polish, Rewrite, Shorten, Expand)
   - Hashtag generation (considers brand niche)
   - Pre-flight analysis (flags banned words as errors)

## Prompt Priority Order

When a voice profile is active, the AI prompt includes:
1. Platform rules (character limits, best practices)
2. Voice Profile (description, do/don't rules, banned words, examples)
3. Goal (Engage, Promote, Announce, Educate, Story)
4. Tone (Professional, Casual, Funny, etc.)
5. Length (Short, Medium, Long)
6. Toggles (Emojis, Hashtags, CTA, Question)

## API Endpoints

### Voice Profile CRUD

```
GET    /api/ai/voice-profiles          - List all profiles (summaries)
GET    /api/ai/voice-profiles/{id}     - Get full profile details
POST   /api/ai/voice-profiles          - Create new profile
PUT    /api/ai/voice-profiles/{id}     - Update profile
DELETE /api/ai/voice-profiles/{id}     - Delete profile
```

### Using Voice Profile in AI Requests

Add `voiceProfileId` to any AI text request:

```json
// POST /api/ai/text
{
  "action": "Hashtags",
  "platform": "Instagram",
  "text": "Check out our new product...",
  "voiceProfileId": "uuid-here"
}

// POST /api/ai/text/generate
{
  "platform": "Instagram",
  "inputText": "Check out our new product...",
  "goal": "Promote",
  "tone": "Casual",
  "length": "Medium",
  "voiceProfileId": "uuid-here"
}
```

## Database Schema

```sql
CREATE TABLE AiVoiceProfiles (
    Id UUID PRIMARY KEY,
    UserId UUID NOT NULL,
    Name VARCHAR(100) NOT NULL,
    Description VARCHAR(1000),
    DoRules VARCHAR(2000),
    DontRules VARCHAR(2000),
    BannedWords VARCHAR(1000),
    ExamplePosts VARCHAR(5000),
    CreatedAt TIMESTAMP WITH TIME ZONE,
    UpdatedAt TIMESTAMP WITH TIME ZONE
);

CREATE INDEX IX_AiVoiceProfiles_UserId ON AiVoiceProfiles(UserId);
```

## Caching

Voice profile is included in cache keys to ensure:
- Profile edits invalidate cached results
- Different profiles get different cached results

Cache key format includes: `{profileId}:{updatedAt.Ticks}`

## Field Guidelines

| Field | Max Length | Format |
|-------|-----------|--------|
| Name | 100 chars | Plain text |
| Description | 1000 chars | Plain text |
| Do Rules | 2000 chars | One rule per line |
| Don't Rules | 2000 chars | One rule per line |
| Banned Words | 1000 chars | Comma-separated or one per line |
| Example Posts | 5000 chars | Separate examples with blank lines |

## Example Voice Profile

**Name**: Tech Startup Blog

**Description**: Tech-savvy millennials interested in productivity and personal development. Our brand is approachable, smart, and slightly irreverent.

**Do Rules**:
```
Use active voice
Include specific numbers and statistics
Start with a hook or question
Keep sentences short and punchy
Use "you" to address the reader directly
```

**Don't Rules**:
```
Don't use corporate jargon
Avoid passive voice
Never be condescending
Don't use clickbait headlines
Avoid overused phrases like "game-changer"
```

**Banned Words**:
```
synergy, leverage, disrupt, pivot, scalable, bandwidth, low-hanging fruit
```

**Example Posts**:
```
Just shipped a feature that saves our users 2 hours per week. Here's the wild part: it took us 3 days to build.

Stop treating your calendar like a to-do list. Your calendar is for commitments. Everything else? That's what your task manager is for.
```

## Local Development

1. Run the migration:
   ```bash
   cd backend
   dotnet ef database update
   ```

2. Start the backend:
   ```bash
   dotnet run
   ```

3. Create a voice profile via the UI or API

4. Select it in the AI Assist panel and generate content
