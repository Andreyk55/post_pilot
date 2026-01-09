using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

public class Post
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public string? MediaUrl { get; set; }
    public Platform Platform { get; set; }
    public DateTime ScheduledAt { get; set; }
    public PostStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
