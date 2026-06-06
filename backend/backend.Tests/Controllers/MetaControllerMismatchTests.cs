using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Providers;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Pins the controller mapping for the permanent-binding rule: a
/// <see cref="ProviderAccountMismatchException"/> from the OAuth service must
/// surface as a 409 Conflict (not a 500/400) on both write endpoints, so the
/// frontend can show "reconnect the original account" rather than a generic error.
/// </summary>
public class MetaControllerMismatchTests
{
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static MetaController NewController(Mock<IMetaOAuthService> meta)
    {
        var user = new Mock<ICurrentUserProvider>();
        user.Setup(u => u.GetCurrentUserId()).Returns(UserId);
        var workspace = new Mock<ICurrentWorkspaceProvider>();
        return new MetaController(
            meta.Object, user.Object, workspace.Object, NullLogger<MetaController>.Instance);
    }

    [Fact]
    public async Task CompleteOAuth_returns_409_on_account_mismatch()
    {
        var meta = new Mock<IMetaOAuthService>();
        meta.Setup(m => m.CompleteOAuthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()))
            .ThrowsAsync(new ProviderAccountMismatchException(ProviderType.Meta, "alpha", "beta"));

        var result = await NewController(meta)
            .CompleteOAuth(new MetaOAuthCompleteRequest("code", "state"));

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task SaveConnection_returns_409_on_account_mismatch()
    {
        var meta = new Mock<IMetaOAuthService>();
        meta.Setup(m => m.SaveConnectionAsync(
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<Guid>()))
            .ThrowsAsync(new ProviderAccountMismatchException(ProviderType.Meta, "alpha", "beta"));

        var result = await NewController(meta)
            .SaveConnection(new MetaSaveConnectionRequest("temp", new List<string> { "p" }, new List<string>()));

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task HandleCallback_returns_409_on_account_mismatch()
    {
        // The callback path must reject permanent-binding violations as 409 (not 400/500)
        // so the wizard does not open page selection.
        var meta = new Mock<IMetaOAuthService>();
        meta.Setup(m => m.HandleCallbackAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new ProviderAccountMismatchException(ProviderType.Meta, "alpha", "beta"));

        var result = await NewController(meta)
            .HandleCallback(new MetaOAuthCallbackRequest("code", "state"));

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task HandleCallback_returns_409_when_account_owned_by_another_workspace()
    {
        var meta = new Mock<IMetaOAuthService>();
        meta.Setup(m => m.HandleCallbackAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new ProviderOwnedByAnotherWorkspaceException(ProviderType.Meta, "alpha"));

        var result = await NewController(meta)
            .HandleCallback(new MetaOAuthCallbackRequest("code", "state"));

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
        // The body carries the permanent-ownership message verbatim for the UI.
        var body = conflict.Value!;
        var errorProp = body.GetType().GetProperty("error")!.GetValue(body) as string;
        Assert.Equal(ProviderOwnedByAnotherWorkspaceException.UserMessage, errorProp);
    }
}
