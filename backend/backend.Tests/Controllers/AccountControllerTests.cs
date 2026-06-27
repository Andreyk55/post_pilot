using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Account;
using PostPilot.Api.Services.Auth;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Account-deletion controller: confirmation-phrase gate, derivation of the target
/// from the auth principal (never the body), and service invocation.
/// </summary>
public class AccountControllerTests
{
    private static readonly Guid AuthUserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly Mock<IAccountDeletionService> _deletion = new();
    private readonly Mock<ICurrentUserProvider> _currentUser = new();

    private AccountController NewController()
    {
        _currentUser.Setup(u => u.GetCurrentUserId()).Returns(AuthUserId);

        var controller = new AccountController(
            _deletion.Object, _currentUser.Object, NullLogger<AccountController>.Instance);

        // SignOutAsync needs an IAuthenticationService in RequestServices.
        var authService = new Mock<IAuthenticationService>();
        authService
            .Setup(a => a.SignOutAsync(It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<AuthenticationProperties>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(authService.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
        };
        return controller;
    }

    [Fact]
    public async Task Correct_phrase_deletes_authenticated_user_and_returns_ok()
    {
        var result = await NewController().DeleteAccount(
            new DeleteAccountRequest("DELETE MY ACCOUNT"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _deletion.Verify(d => d.DeleteCurrentAccountAsync(AuthUserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("delete my account")]
    [InlineData("DELETE ACCOUNT")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Wrong_phrase_is_rejected_and_nothing_is_deleted(string? phrase)
    {
        var result = await NewController().DeleteAccount(
            new DeleteAccountRequest(phrase), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _deletion.Verify(d => d.DeleteCurrentAccountAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Target_is_always_the_principal_never_the_body()
    {
        // The DTO has no userId field by design; the controller derives the target from
        // ICurrentUserProvider. Confirm the service is called with the principal id only.
        await NewController().DeleteAccount(new DeleteAccountRequest("DELETE MY ACCOUNT"), CancellationToken.None);

        _deletion.Verify(d => d.DeleteCurrentAccountAsync(
            It.Is<Guid>(id => id == AuthUserId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Post_alias_behaves_like_delete()
    {
        var result = await NewController().DeleteAccountViaPost(
            new DeleteAccountRequest("DELETE MY ACCOUNT"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _deletion.Verify(d => d.DeleteCurrentAccountAsync(AuthUserId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
