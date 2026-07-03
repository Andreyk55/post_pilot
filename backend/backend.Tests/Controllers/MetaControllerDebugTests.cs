using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// M4: the Meta instagram/debug endpoint must be hidden (404) in production unless explicitly
/// enabled, and must never run its provider-introspection when disabled.
/// </summary>
public class MetaControllerDebugTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private static MetaController NewController(
        Mock<IMetaOAuthService> meta, string environmentName, bool enableDebug)
    {
        var user = new Mock<ICurrentUserProvider>();
        var workspace = new Mock<ICurrentWorkspaceProvider>();
        workspace.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkspaceId);

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        return new MetaController(
            meta.Object, user.Object, workspace.Object,
            env.Object, new MetaOptions { EnableDebugEndpoints = enableDebug },
            NullLogger<MetaController>.Instance);
    }

    [Fact]
    public async Task Debug_returns_404_in_production_when_disabled()
    {
        // Strict mock: the service must NOT be touched when the endpoint is gated off.
        var meta = new Mock<IMetaOAuthService>(MockBehavior.Strict);

        var result = await NewController(meta, "Production", enableDebug: false)
            .DebugInstagramDiscovery();

        Assert.IsType<NotFoundResult>(result.Result);
        meta.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Debug_runs_in_development()
    {
        var meta = new Mock<IMetaOAuthService>();
        meta.Setup(m => m.DebugInstagramDiscoveryAsync(WorkspaceId))
            .ReturnsAsync(new { pageCount = 0 });

        var result = await NewController(meta, "Development", enableDebug: false)
            .DebugInstagramDiscovery();

        Assert.IsType<OkObjectResult>(result.Result);
        meta.Verify(m => m.DebugInstagramDiscoveryAsync(WorkspaceId), Times.Once);
    }

    [Fact]
    public async Task Debug_runs_in_production_when_flag_enabled()
    {
        var meta = new Mock<IMetaOAuthService>();
        meta.Setup(m => m.DebugInstagramDiscoveryAsync(WorkspaceId))
            .ReturnsAsync(new { pageCount = 0 });

        var result = await NewController(meta, "Production", enableDebug: true)
            .DebugInstagramDiscovery();

        Assert.IsType<OkObjectResult>(result.Result);
        meta.Verify(m => m.DebugInstagramDiscoveryAsync(WorkspaceId), Times.Once);
    }
}
