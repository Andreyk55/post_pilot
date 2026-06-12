namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for the AI rate limiter.
/// Bound from "Ai:RateLimiter" config section. All defaults in appsettings.common.json.
/// </summary>
public class AiRateLimiterOptions
{
    /// <summary>
    /// Max AI calls allowed per user within the window (the default quota).
    /// </summary>
    public int MaxCallsPerDay { get; set; }

    /// <summary>
    /// Window size in hours for rate limiting.
    /// </summary>
    public int WindowHours { get; set; }

    /// <summary>
    /// Quota applied to users whose internal PostPilot user ID is listed in
    /// <see cref="OverrideUserIds"/>. If missing or not greater than zero, the
    /// override falls back to <see cref="MaxCallsPerDay"/>.
    /// </summary>
    public int OverrideMaxCallsPerDay { get; set; }

    /// <summary>
    /// Comma-separated list of internal PostPilot user IDs (GUIDs) that receive
    /// <see cref="OverrideMaxCallsPerDay"/> instead of <see cref="MaxCallsPerDay"/>.
    /// Bound as a single string from "Ai:RateLimiter:OverrideUserIds"
    /// (env: Ai__RateLimiter__OverrideUserIds). Whitespace around each ID is trimmed
    /// and comparison is case-insensitive. There is no per-user quota value — every
    /// listed user shares the single override quota.
    /// </summary>
    public string? OverrideUserIds { get; set; }

    /// <summary>
    /// Resolves the effective quota for a given user ID.
    /// Returns <see cref="OverrideMaxCallsPerDay"/> when the user is in the override
    /// list AND the override value is valid (&gt; 0); otherwise returns
    /// <see cref="MaxCallsPerDay"/>.
    /// </summary>
    public int GetMaxCallsForUser(Guid userId)
    {
        if (OverrideMaxCallsPerDay > 0 && IsOverrideUser(userId))
        {
            return OverrideMaxCallsPerDay;
        }

        return MaxCallsPerDay;
    }

    /// <summary>
    /// Whether the given user ID appears in the comma-separated <see cref="OverrideUserIds"/>.
    /// Parsing trims whitespace and compares case-insensitively.
    /// </summary>
    public bool IsOverrideUser(Guid userId)
    {
        if (string.IsNullOrWhiteSpace(OverrideUserIds))
        {
            return false;
        }

        var target = userId.ToString();

        foreach (var rawId in OverrideUserIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = rawId.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
