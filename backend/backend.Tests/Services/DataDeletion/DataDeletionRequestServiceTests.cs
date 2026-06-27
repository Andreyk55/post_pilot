using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.DataDeletion;
using Xunit;

namespace PostPilot.Api.Tests.Services.DataDeletion;

public class DataDeletionRequestServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly DataDeletionRequestService _service;

    public DataDeletionRequestServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _service = new DataDeletionRequestService(_db, NullLogger<DataDeletionRequestService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateProcessing_persists_url_safe_alphanumeric_code()
    {
        var request = await _service.CreateProcessingAsync(ProviderType.Meta, "user-1", CancellationToken.None);

        Assert.Equal(DataDeletionStatus.Processing, request.Status);
        Assert.Matches(new Regex("^[A-Za-z0-9]+$"), request.ConfirmationCode);
        Assert.True(request.ConfirmationCode.Length >= 16);
        Assert.True(await _db.DataDeletionRequests.AnyAsync(r => r.ConfirmationCode == request.ConfirmationCode));
    }

    [Fact]
    public async Task Confirmation_codes_are_unique_across_requests()
    {
        var codes = new HashSet<string>();
        for (var i = 0; i < 25; i++)
        {
            var r = await _service.CreateProcessingAsync(ProviderType.Meta, $"u{i}", CancellationToken.None);
            Assert.True(codes.Add(r.ConfirmationCode));
        }
    }

    [Fact]
    public async Task MarkCompleted_sets_status_and_audit_fields()
    {
        var request = await _service.CreateProcessingAsync(ProviderType.Meta, "u", CancellationToken.None);
        var userId = Guid.NewGuid();
        var wsId = Guid.NewGuid();

        await _service.MarkCompletedAsync(request.ConfirmationCode, userId, wsId, "a warning", CancellationToken.None);

        var saved = await _db.DataDeletionRequests.AsNoTracking().FirstAsync(r => r.ConfirmationCode == request.ConfirmationCode);
        Assert.Equal(DataDeletionStatus.Completed, saved.Status);
        Assert.Equal(userId, saved.UserId);
        Assert.Equal(wsId, saved.WorkspaceId);
        Assert.Equal("a warning", saved.Warning);
        Assert.NotNull(saved.CompletedAt);
    }

    [Fact]
    public async Task MarkAlreadyDeleted_and_MarkFailed_set_status()
    {
        var a = await _service.CreateProcessingAsync(ProviderType.Meta, "u", CancellationToken.None);
        var b = await _service.CreateProcessingAsync(ProviderType.Meta, "u", CancellationToken.None);

        await _service.MarkAlreadyDeletedAsync(a.ConfirmationCode, CancellationToken.None);
        await _service.MarkFailedAsync(b.ConfirmationCode, "safe error", CancellationToken.None);

        var sa = await _db.DataDeletionRequests.AsNoTracking().FirstAsync(r => r.ConfirmationCode == a.ConfirmationCode);
        var sb = await _db.DataDeletionRequests.AsNoTracking().FirstAsync(r => r.ConfirmationCode == b.ConfirmationCode);
        Assert.Equal(DataDeletionStatus.AlreadyDeleted, sa.Status);
        Assert.Equal(DataDeletionStatus.Failed, sb.Status);
        Assert.Equal("safe error", sb.Error);
    }

    [Fact]
    public async Task GetStatus_returns_null_for_unknown_code()
    {
        Assert.Null(await _service.GetStatusAsync("does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task GetStatus_projects_public_fields_only()
    {
        var request = await _service.CreateProcessingAsync(ProviderType.Meta, "secret-account-id", CancellationToken.None);

        var dto = await _service.GetStatusAsync(request.ConfirmationCode, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(request.ConfirmationCode, dto!.ConfirmationCode);
        Assert.Equal("Meta", dto.Provider);
        Assert.Equal("Processing", dto.Status);
        // The DTO type itself carries no userId/workspaceId/providerAccountId fields.
    }
}
