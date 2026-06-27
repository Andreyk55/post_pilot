using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// EF-backed <see cref="IDataDeletionRequestService"/>. Confirmation codes are random
/// 32-char URL-safe alphanumerics minted with a CSPRNG — never derived from a DB id.
/// </summary>
public sealed class DataDeletionRequestService : IDataDeletionRequestService
{
    // URL-safe alphanumeric alphabet (no ambiguous separators). 32 chars ≈ 190 bits.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private const int CodeLength = 32;

    private readonly AppDbContext _context;
    private readonly ILogger<DataDeletionRequestService> _logger;

    public DataDeletionRequestService(AppDbContext context, ILogger<DataDeletionRequestService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<DataDeletionRequest> CreateProcessingAsync(
        ProviderType provider,
        string providerAccountId,
        CancellationToken ct)
    {
        var request = new DataDeletionRequest
        {
            Id = Guid.NewGuid(),
            ConfirmationCode = await GenerateUniqueCodeAsync(ct),
            Provider = provider,
            ProviderAccountId = providerAccountId,
            Status = DataDeletionStatus.Processing,
            RequestedAt = DateTime.UtcNow,
        };

        _context.DataDeletionRequests.Add(request);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created data-deletion request {ConfirmationCode} for provider {Provider}.",
            request.ConfirmationCode, provider);

        return request;
    }

    public async Task MarkCompletedAsync(
        string confirmationCode, Guid? userId, Guid? workspaceId, string? warning, CancellationToken ct)
    {
        var request = await FindAsync(confirmationCode, ct);
        if (request is null) return;

        request.Status = DataDeletionStatus.Completed;
        request.UserId = userId;
        request.WorkspaceId = workspaceId;
        request.Warning = Truncate(warning, 1000);
        request.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    public async Task MarkAlreadyDeletedAsync(string confirmationCode, CancellationToken ct)
    {
        var request = await FindAsync(confirmationCode, ct);
        if (request is null) return;

        request.Status = DataDeletionStatus.AlreadyDeleted;
        request.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(string confirmationCode, string safeError, CancellationToken ct)
    {
        var request = await FindAsync(confirmationCode, ct);
        if (request is null) return;

        request.Status = DataDeletionStatus.Failed;
        request.Error = Truncate(safeError, 1000);
        request.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    public async Task<DataDeletionStatusDto?> GetStatusAsync(string confirmationCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(confirmationCode)) return null;

        return await _context.DataDeletionRequests
            .AsNoTracking()
            .Where(r => r.ConfirmationCode == confirmationCode)
            .Select(r => new DataDeletionStatusDto(
                r.ConfirmationCode,
                r.Provider.ToString(),
                r.Status.ToString(),
                r.RequestedAt,
                r.CompletedAt))
            .FirstOrDefaultAsync(ct);
    }

    private Task<DataDeletionRequest?> FindAsync(string confirmationCode, CancellationToken ct) =>
        _context.DataDeletionRequests.FirstOrDefaultAsync(r => r.ConfirmationCode == confirmationCode, ct);

    private async Task<string> GenerateUniqueCodeAsync(CancellationToken ct)
    {
        // Collisions are astronomically unlikely at 32 chars, but loop defensively so a
        // duplicate can never throw on the unique index.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = GenerateCode();
            var exists = await _context.DataDeletionRequests.AnyAsync(r => r.ConfirmationCode == code, ct);
            if (!exists) return code;
        }
        throw new InvalidOperationException("Failed to generate a unique confirmation code.");
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        Span<byte> buffer = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(buffer);
        for (var i = 0; i < CodeLength; i++)
        {
            chars[i] = CodeAlphabet[buffer[i] % CodeAlphabet.Length];
        }
        return new string(chars);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
