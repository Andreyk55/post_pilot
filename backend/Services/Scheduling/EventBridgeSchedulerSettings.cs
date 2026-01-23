namespace PostPilot.Api.Services.Scheduling;

/// <summary>
/// Configuration settings for AWS EventBridge Scheduler
/// </summary>
public class EventBridgeSchedulerSettings
{
    /// <summary>
    /// The EventBridge Scheduler group name for PostPilot schedules
    /// </summary>
    public string ScheduleGroupName { get; set; } = "postpilot-schedules";

    /// <summary>
    /// ARN of the Lambda function that handles post publishing
    /// </summary>
    public string PublisherLambdaArn { get; set; } = "";

    /// <summary>
    /// ARN of the IAM role that EventBridge assumes to invoke the target
    /// </summary>
    public string SchedulerRoleArn { get; set; } = "";

    /// <summary>
    /// Base URL for the API (used for HTTP target if not using Lambda)
    /// </summary>
    public string? ApiBaseUrl { get; set; }
}
