using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Support;
using Xunit;

namespace PostPilot.Api.Tests.Services.Support;

/// <summary>
/// Support ("Contact Us") service: validation, trimming, length caps, per-user rate
/// limiting, and that every row is created as <see cref="SupportContactStatus.New"/> with
/// the supplied (auth-derived) UserId and optional WorkspaceId.
/// </summary>
public class SupportContactServiceTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private readonly AppDbContext _db;
    private readonly SupportContactService _service;

    public SupportContactServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _service = new SupportContactService(_db, NullLogger<SupportContactService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static CreateSupportContactRequest Req(
        string? subject = "Help me", string? message = "Something is broken", SupportCategory? category = null) =>
        new(category, subject, message);

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_request_with_status_New_and_stored_user_and_workspace()
    {
        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);

        Assert.Equal("New", response.Status);
        Assert.NotEqual(Guid.Empty, response.Id);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Equal(SupportContactStatus.New, saved.Status);
        Assert.Equal(UserId, saved.UserId);
        Assert.Equal(WorkspaceId, saved.WorkspaceId);
        Assert.Equal(response.Id, saved.Id);
    }

    [Fact]
    public async Task Stores_null_workspace_when_none_provided()
    {
        await _service.CreateAsync(UserId, null, Req(), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Null(saved.WorkspaceId);
    }

    [Fact]
    public async Task Persists_optional_category_when_provided()
    {
        await _service.CreateAsync(
            UserId, WorkspaceId, Req(category: SupportCategory.DataDeletion), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Equal(SupportCategory.DataDeletion, saved.Category);
    }

    [Fact]
    public async Task Null_category_is_allowed()
    {
        await _service.CreateAsync(UserId, WorkspaceId, Req(category: null), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Null(saved.Category);
    }

    [Fact]
    public async Task Trims_subject_and_message()
    {
        await _service.CreateAsync(
            UserId, WorkspaceId, Req(subject: "  Hi  ", message: "  body  "), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Equal("Hi", saved.Subject);
        Assert.Equal("body", saved.Message);
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Subject_is_required(string? subject)
    {
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(subject: subject), CancellationToken.None));
        Assert.Contains("subject", ex.Errors.Keys);
        Assert.Empty(await _db.SupportContactRequests.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Message_is_required(string? message)
    {
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(message: message), CancellationToken.None));
        Assert.Contains("message", ex.Errors.Keys);
        Assert.Empty(await _db.SupportContactRequests.ToListAsync());
    }

    [Fact]
    public async Task Subject_max_length_is_enforced()
    {
        var tooLong = new string('s', ValidationLimits.SupportSubjectMaxLength + 1);
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(subject: tooLong), CancellationToken.None));
        Assert.Contains("subject", ex.Errors.Keys);
    }

    [Fact]
    public async Task Message_max_length_is_enforced()
    {
        var tooLong = new string('m', ValidationLimits.SupportMessageMaxLength + 1);
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(message: tooLong), CancellationToken.None));
        Assert.Contains("message", ex.Errors.Keys);
    }

    [Fact]
    public async Task Subject_at_max_length_is_accepted()
    {
        var atMax = new string('s', ValidationLimits.SupportSubjectMaxLength);
        await _service.CreateAsync(UserId, WorkspaceId, Req(subject: atMax), CancellationToken.None);
        Assert.Equal(1, await _db.SupportContactRequests.CountAsync());
    }

    [Fact]
    public async Task Unknown_category_is_rejected()
    {
        var bogus = (SupportCategory)999;
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(category: bogus), CancellationToken.None));
        Assert.Contains("category", ex.Errors.Keys);
    }

    // ── Rate limiting ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Per_user_rate_limit_blocks_after_window_cap()
    {
        for (var i = 0; i < ValidationLimits.SupportMaxRequestsPerWindow; i++)
        {
            await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);
        }

        await Assert.ThrowsAsync<SupportRateLimitExceededException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None));

        // Exactly the cap was persisted — the over-limit attempt created nothing.
        Assert.Equal(
            ValidationLimits.SupportMaxRequestsPerWindow,
            await _db.SupportContactRequests.CountAsync());
    }

    [Fact]
    public async Task Rate_limit_is_per_user_other_users_unaffected()
    {
        var otherUser = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
        for (var i = 0; i < ValidationLimits.SupportMaxRequestsPerWindow; i++)
        {
            await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);
        }

        // A different user is not blocked by the first user's volume.
        var response = await _service.CreateAsync(otherUser, null, Req(), CancellationToken.None);
        Assert.Equal("New", response.Status);
    }

    [Fact]
    public async Task Old_requests_outside_window_do_not_count_toward_limit()
    {
        // Seed cap-worth of requests dated before the rolling window.
        var stale = DateTime.UtcNow.AddHours(-(ValidationLimits.SupportRateLimitWindowHours + 1));
        for (var i = 0; i < ValidationLimits.SupportMaxRequestsPerWindow; i++)
        {
            _db.SupportContactRequests.Add(new SupportContactRequest
            {
                Id = Guid.NewGuid(), UserId = UserId, Subject = "old", Message = "old",
                Status = SupportContactStatus.New, CreatedAt = stale,
            });
        }
        await _db.SaveChangesAsync();

        // A fresh request still succeeds because the stale ones are outside the window.
        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);
        Assert.Equal("New", response.Status);
    }
}
