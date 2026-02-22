namespace PostPilot.Api.Middleware;

/// <summary>
/// Middleware that ensures every HTTP request has a CorrelationId and exposes it in the
/// logging scope so every log line emitted during the request includes the id.
///
/// Header: X-Correlation-Id
///   – If present on the incoming request, that value is reused.
///   – Otherwise a new GUID is generated.
/// The id is echoed back on the response in the same header.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
                            ?? Guid.NewGuid().ToString("N")[..12]; // short 12-char id for readability

        // Expose on HttpContext so controllers/services can retrieve it if needed
        context.Items["CorrelationId"] = correlationId;

        // Echo back to caller
        context.Response.Headers[HeaderName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId,
                   ["RequestPath"]   = context.Request.Path.Value ?? string.Empty,
               }))
        {
            await _next(context);
        }
    }
}
