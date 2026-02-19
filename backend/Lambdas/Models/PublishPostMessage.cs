namespace PostPilot.Api.Lambdas.Models;

/// <summary>
/// SQS message sent from Dispatcher to Publisher Lambda.
/// </summary>
public class PublishPostMessage
{
    public Guid PostId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string PostType { get; set; } = "Feed";
}
