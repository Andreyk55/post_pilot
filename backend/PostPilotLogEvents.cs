using Microsoft.Extensions.Logging;

namespace PostPilot.Api;

/// <summary>
/// Centralised EventId constants for structured logging across the PostPilot app.
/// Range assignments:
///   1000–1999  HTTP request lifecycle
///   2000–2099  Post publishing pipeline
///   2100–2199  Outbound HTTP calls
///   3000–3099  Retry engine
/// </summary>
public static class PostPilotLogEvents
{
    // ── HTTP request lifecycle ──────────────────────────────────────────────
    public static readonly EventId RequestStart = new(1000, nameof(RequestStart));
    public static readonly EventId RequestEnd   = new(1001, nameof(RequestEnd));

    // ── Publishing pipeline ─────────────────────────────────────────────────
    public static readonly EventId PublishStart   = new(2000, nameof(PublishStart));
    public static readonly EventId PublishAttempt = new(2001, nameof(PublishAttempt));
    public static readonly EventId PublishSuccess = new(2002, nameof(PublishSuccess));
    public static readonly EventId PublishFail    = new(2003, nameof(PublishFail));

    // ── Outbound HTTP calls ─────────────────────────────────────────────────
    public static readonly EventId OutboundCall  = new(2100, nameof(OutboundCall));
    public static readonly EventId OutboundError = new(2101, nameof(OutboundError));

    // ── Retry engine ────────────────────────────────────────────────────────
    public static readonly EventId RetryScheduled = new(3000, nameof(RetryScheduled));
    public static readonly EventId RetryStart     = new(3001, nameof(RetryStart));
    public static readonly EventId RetryStop      = new(3002, nameof(RetryStop));
}
