using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.DataDeletion;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Controller-level behavior for the public Meta data-deletion callback + status.
/// Verifies the required JSON shape, request tracking, status mapping, and that an
/// invalid signature deletes nothing.
/// </summary>
public class DataDeletionControllerTests
{
    private const string FrontendUrl = "https://post-auto-pilot.vercel.app";

    private readonly Mock<IMetaSignedRequestVerifier> _verifier = new();
    private readonly Mock<IDataDeletionRequestService> _requests = new();
    private readonly Mock<IMetaDataDeletionService> _meta = new();

    private DataDeletionController NewController() => new(
        _verifier.Object, _requests.Object, _meta.Object,
        new AuthOptions { FrontendUrl = FrontendUrl },
        NullLogger<DataDeletionController>.Instance);

    private void SetupCreate(string code) =>
        _requests.Setup(r => r.CreateProcessingAsync(It.IsAny<ProviderType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DataDeletionRequest { ConfirmationCode = code, Provider = ProviderType.Meta });

    [Fact]
    public async Task Valid_callback_returns_url_and_confirmation_code_and_marks_completed()
    {
        _verifier.Setup(v => v.Verify(It.IsAny<string>())).Returns(new MetaSignedRequestPayload("user-123"));
        SetupCreate("CODE123");
        _meta.Setup(m => m.PurgeByProviderAccountIdAsync("user-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetaDataDeletionResult(
                DataDeletionStatus.Completed, Guid.NewGuid(), Guid.NewGuid(),
                new Dictionary<string, int>(), Array.Empty<string>()));

        var result = await NewController().MetaDataDeletion("signed", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<MetaDataDeletionCallbackResponse>(ok.Value);
        Assert.Equal("CODE123", body.ConfirmationCode);
        Assert.Equal($"{FrontendUrl}/data-deletion/status/CODE123", body.Url);
        _requests.Verify(r => r.MarkCompletedAsync("CODE123", It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Callback_marks_already_deleted_when_no_connection()
    {
        _verifier.Setup(v => v.Verify(It.IsAny<string>())).Returns(new MetaSignedRequestPayload("ghost"));
        SetupCreate("CODE999");
        _meta.Setup(m => m.PurgeByProviderAccountIdAsync("ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MetaDataDeletionResult.AlreadyDeleted());

        var result = await NewController().MetaDataDeletion("signed", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _requests.Verify(r => r.MarkAlreadyDeletedAsync("CODE999", It.IsAny<CancellationToken>()), Times.Once);
        _requests.Verify(r => r.MarkCompletedAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Invalid_signature_deletes_nothing_and_returns_400()
    {
        _verifier.Setup(v => v.Verify(It.IsAny<string>()))
            .Throws(new InvalidSignedRequestException("Invalid signed_request signature."));

        var result = await NewController().MetaDataDeletion("tampered", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _requests.Verify(r => r.CreateProcessingAsync(It.IsAny<ProviderType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _meta.Verify(m => m.PurgeByProviderAccountIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unexpected_purge_failure_still_returns_code_and_marks_failed()
    {
        _verifier.Setup(v => v.Verify(It.IsAny<string>())).Returns(new MetaSignedRequestPayload("user-x"));
        SetupCreate("CODEERR");
        _meta.Setup(m => m.PurgeByProviderAccountIdAsync("user-x", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var result = await NewController().MetaDataDeletion("signed", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<MetaDataDeletionCallbackResponse>(ok.Value);
        Assert.Equal("CODEERR", body.ConfirmationCode);
        _requests.Verify(r => r.MarkFailedAsync("CODEERR", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Status_endpoint_returns_projection_without_internal_ids()
    {
        var requestedAt = DateTime.UtcNow;
        _requests.Setup(r => r.GetStatusAsync("CODE123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DataDeletionStatusDto("CODE123", "Meta", "Completed", requestedAt, requestedAt));

        var result = await NewController().GetStatus("CODE123", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<DataDeletionStatusResponse>(ok.Value);
        Assert.Equal("CODE123", body.ConfirmationCode);
        Assert.Equal("Completed", body.Status);
        Assert.Equal("Meta", body.Provider);
    }

    [Fact]
    public async Task Status_endpoint_returns_404_for_unknown_code()
    {
        _requests.Setup(r => r.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DataDeletionStatusDto?)null);

        var result = await NewController().GetStatus("nope", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
