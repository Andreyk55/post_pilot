using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Support;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Support ("Contact Us") controller: enforces authentication, derives the sender from the
/// auth principal (never the body), resolves the workspace best-effort, and maps service
/// validation / rate-limit failures to 400 / 429.
/// </summary>
public class SupportControllerTests
{
    private static readonly Guid AuthUserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private readonly Mock<ISupportContactService> _support = new();
    private readonly Mock<ICurrentUserProvider> _currentUser = new();
    private readonly Mock<ICurrentWorkspaceProvider> _currentWorkspace = new();

    private SupportController NewController()
    {
        _currentUser.Setup(u => u.GetCurrentUserId()).Returns(AuthUserId);
        _currentWorkspace
            .Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkspaceId);
        _support
            .Setup(s => s.CreateAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CreateSupportContactRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, Guid? w, CreateSupportContactRequest r, CancellationToken _) =>
                new SupportContactResponse(Guid.NewGuid(), "New", DateTime.UtcNow));

        return new SupportController(
            _support.Object, _currentUser.Object, _currentWorkspace.Object);
    }

    private static CreateSupportContactRequest Body(
        string? subject = "Need help", string? message = "Please help", SupportCategory? category = null) =>
        new(category, subject, message);

    // ── Auth / endpoint shape ──────────────────────────────────────────────────

    [Fact]
    public void Controller_requires_authentication()
    {
        Assert.NotNull(typeof(SupportController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(typeof(SupportController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Contact_is_a_post_to_contact_route()
    {
        var method = typeof(SupportController).GetMethod(nameof(SupportController.Contact))!;
        var post = method.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(post);
        Assert.Equal("contact", post!.Template);
    }

    /// <summary>
    /// The request DTO structurally cannot carry a userId/accountId/email: it has only
    /// category/subject/message. So a client that adds those keys to the JSON has them
    /// silently ignored at model binding — the target is always the auth principal.
    /// </summary>
    [Fact]
    public void Request_dto_exposes_only_category_subject_message()
    {
        var props = typeof(CreateSupportContactRequest)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Category", "Subject", "Message",
        }, props);

        Assert.DoesNotContain("UserId", props);
        Assert.DoesNotContain("AccountId", props);
        Assert.DoesNotContain("Email", props);
    }

    // ── Behavior ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authenticated_user_can_create_and_gets_201()
    {
        var result = await NewController().Contact(Body(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, objectResult.StatusCode);
        Assert.IsType<SupportContactResponse>(objectResult.Value);
    }

    [Fact]
    public async Task Sender_is_always_the_principal_never_the_body()
    {
        await NewController().Contact(Body(), CancellationToken.None);

        _support.Verify(s => s.CreateAsync(
            It.Is<Guid>(id => id == AuthUserId),
            It.IsAny<Guid?>(),
            It.IsAny<CreateSupportContactRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Passes_resolved_workspace_when_available()
    {
        await NewController().Contact(Body(), CancellationToken.None);

        _support.Verify(s => s.CreateAsync(
            AuthUserId,
            It.Is<Guid?>(w => w == WorkspaceId),
            It.IsAny<CreateSupportContactRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Passes_null_workspace_when_none_is_selected()
    {
        var controller = NewController();
        _currentWorkspace
            .Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkspaceNotSelectedException("none"));

        var result = await controller.Contact(Body(), CancellationToken.None);

        // Still succeeds — Contact Us works with no workspace selected.
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        _support.Verify(s => s.CreateAsync(
            AuthUserId, null, It.IsAny<CreateSupportContactRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Passes_null_workspace_when_access_denied()
    {
        var controller = NewController();
        _currentWorkspace
            .Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkspaceAccessDeniedException("denied"));

        var result = await controller.Contact(Body(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        _support.Verify(s => s.CreateAsync(
            AuthUserId, null, It.IsAny<CreateSupportContactRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Validation_failure_maps_to_400()
    {
        var controller = NewController();
        var errors = new Dictionary<string, string[]> { ["subject"] = ["Subject is required."] };
        _support
            .Setup(s => s.CreateAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CreateSupportContactRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SupportValidationException(errors));

        var result = await controller.Contact(Body(subject: ""), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    [Fact]
    public async Task Rate_limit_maps_to_429()
    {
        var controller = NewController();
        _support
            .Setup(s => s.CreateAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CreateSupportContactRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SupportRateLimitExceededException("too many"));

        var result = await controller.Contact(Body(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
    }
}
