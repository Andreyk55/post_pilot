using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Middleware;
using PostPilot.Api.Services.Auth;
using Xunit;

namespace PostPilot.Api.Tests.Middleware;

/// <summary>
/// Pins the HTTP contract for workspace-resolution failures: a stale/missing selected
/// workspace is a 409 (client must re-select), and an unauthorized workspace is a 403.
/// Neither path may silently succeed.
/// </summary>
public class WorkspaceResolutionExceptionMiddlewareTests
{
    private static async Task<(int status, string body)> RunWith(Exception toThrow)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        RequestDelegate next = _ => throw toThrow;
        var mw = new WorkspaceResolutionExceptionMiddleware(
            next, NullLogger<WorkspaceResolutionExceptionMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (ctx.Response.StatusCode, body);
    }

    [Fact]
    public async Task NotSelected_maps_to_409_with_code()
    {
        var (status, body) = await RunWith(new WorkspaceNotSelectedException("pick one"));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("WORKSPACE_NOT_SELECTED", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AccessDenied_maps_to_403_with_code()
    {
        var (status, body) = await RunWith(new WorkspaceAccessDeniedException("nope"));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("WORKSPACE_FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unrelated_exceptions_are_not_swallowed()
    {
        // The middleware must only handle workspace-resolution exceptions; anything
        // else propagates to the host's normal error handling.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWith(new InvalidOperationException("boom")));
    }
}
