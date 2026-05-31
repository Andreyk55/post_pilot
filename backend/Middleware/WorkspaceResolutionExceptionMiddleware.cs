using System.Text.Json;
using PostPilot.Api.Services.Auth;

namespace PostPilot.Api.Middleware;

/// <summary>
/// Translates workspace-resolution failures thrown by <see cref="ICurrentWorkspaceProvider"/>
/// into explicit HTTP responses, so every workspace-scoped endpoint gets consistent
/// behavior without a per-action try/catch:
///
/// <list type="bullet">
///   <item><see cref="WorkspaceNotSelectedException"/> → 409 Conflict
///   (<c>WORKSPACE_NOT_SELECTED</c>) — the selected workspace is missing/stale/deleted;
///   the client must (re)select a workspace.</item>
///   <item><see cref="WorkspaceAccessDeniedException"/> → 403 Forbidden
///   (<c>WORKSPACE_FORBIDDEN</c>) — the user is not a member of the selected workspace.</item>
/// </list>
///
/// The key invariant this enforces: a stale/unauthorized workspace never silently
/// resolves to a different workspace, so no media/post/provider action is created under
/// the wrong account context. The request is short-circuited here with an error.
/// </summary>
public class WorkspaceResolutionExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WorkspaceResolutionExceptionMiddleware> _logger;

    public WorkspaceResolutionExceptionMiddleware(
        RequestDelegate next,
        ILogger<WorkspaceResolutionExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (WorkspaceNotSelectedException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, "WORKSPACE_NOT_SELECTED", ex.Message);
        }
        catch (WorkspaceAccessDeniedException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "WORKSPACE_FORBIDDEN", ex.Message);
        }
    }

    private async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            // Can't change the status now; the partial response is already on the wire.
            // Log so we can see it, but don't throw over the original failure.
            _logger.LogWarning(
                "Workspace resolution failed ({Code}) but response already started; cannot rewrite to {Status}.",
                code, statusCode);
            return;
        }

        _logger.LogWarning("Workspace resolution failed: {Code} -> {Status}. {Message}", code, statusCode, message);

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = JsonSerializer.Serialize(new { error = message, code });
        await context.Response.WriteAsync(payload);
    }
}
