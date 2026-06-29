namespace PostPilot.Api.Entities;

public class UserMediaUploadUsage
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateTime PeriodStartUtc { get; set; }

    public DateTime PeriodEndUtc { get; set; }

    public int UploadCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
