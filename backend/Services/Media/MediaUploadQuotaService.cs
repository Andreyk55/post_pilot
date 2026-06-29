using System.Data;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Media;

public sealed class MediaUploadQuotaService : IMediaUploadQuotaService
{
    private const int MaxConsumeAttempts = 3;

    private readonly AppDbContext _db;
    private readonly MediaUploadQuotaOptions _options;

    public MediaUploadQuotaService(AppDbContext db, MediaUploadQuotaOptions options)
    {
        _db = db;
        _options = options;
    }

    public async Task<MediaUploadQuotaResult> TryConsumeUploadAsync(Guid userId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new MediaUploadQuotaResult(
                Allowed: true,
                Limit: _options.MaxUploadsPerUserPerWindow,
                Used: 0,
                Remaining: _options.MaxUploadsPerUserPerWindow,
                PeriodEndUtc: GetPeriodEndUtc(DateTime.UtcNow));
        }

        for (var attempt = 1; attempt <= MaxConsumeAttempts; attempt++)
        {
            await using var transaction = await BeginQuotaTransactionIfSupportedAsync(ct);

            try
            {
                var result = await TryConsumeOnceAsync(userId, ct);

                if (transaction is not null)
                    await transaction.CommitAsync(ct);

                return result;
            }
            catch (DbUpdateException) when (attempt < MaxConsumeAttempts)
            {
                _db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not consume media upload quota after retrying.");
    }

    private async Task<MediaUploadQuotaResult> TryConsumeOnceAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var periodStartUtc = GetPeriodStartUtc(now);
        var periodEndUtc = periodStartUtc.AddHours(_options.WindowHours);
        var limit = _options.MaxUploadsPerUserPerWindow;

        var usage = await _db.UserMediaUploadUsages
            .SingleOrDefaultAsync(x => x.UserId == userId && x.PeriodStartUtc == periodStartUtc, ct);

        if (usage is null)
        {
            usage = new UserMediaUploadUsage
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PeriodStartUtc = periodStartUtc,
                PeriodEndUtc = periodEndUtc,
                UploadCount = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            _db.UserMediaUploadUsages.Add(usage);
            await _db.SaveChangesAsync(ct);

            return new MediaUploadQuotaResult(true, limit, 1, Math.Max(0, limit - 1), usage.PeriodEndUtc);
        }

        if (usage.UploadCount >= limit)
        {
            return new MediaUploadQuotaResult(
                Allowed: false,
                Limit: limit,
                Used: usage.UploadCount,
                Remaining: 0,
                PeriodEndUtc: usage.PeriodEndUtc,
                ErrorCode: MediaUploadQuotaExceededException.DefaultErrorCode);
        }

        usage.UploadCount += 1;
        usage.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);

        return new MediaUploadQuotaResult(true, limit, usage.UploadCount, Math.Max(0, limit - usage.UploadCount), usage.PeriodEndUtc);
    }

    private DateTime GetPeriodStartUtc(DateTime utcNow)
    {
        var alignedTicks = utcNow.Ticks - (utcNow.Ticks % TimeSpan.FromHours(_options.WindowHours).Ticks);
        return new DateTime(alignedTicks, DateTimeKind.Utc);
    }

    private DateTime GetPeriodEndUtc(DateTime utcNow) => GetPeriodStartUtc(utcNow).AddHours(_options.WindowHours);

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginQuotaTransactionIfSupportedAsync(CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
            return null;

        return await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    }
}

internal sealed class DisabledMediaUploadQuotaService : IMediaUploadQuotaService
{
    public static readonly DisabledMediaUploadQuotaService Instance = new();

    private DisabledMediaUploadQuotaService()
    {
    }

    public Task<MediaUploadQuotaResult> TryConsumeUploadAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult(new MediaUploadQuotaResult(true, int.MaxValue, 0, int.MaxValue, DateTime.UtcNow));
}
