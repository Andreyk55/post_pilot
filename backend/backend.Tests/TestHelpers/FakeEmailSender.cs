using PostPilot.Api.Services.Email;

namespace PostPilot.Api.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="IEmailSender"/> for tests: records every sent message, and can be
/// configured to throw (to exercise the best-effort failure path).
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = new();

    /// <summary>When set, <see cref="SendAsync"/> throws this instead of recording.</summary>
    public Exception? ThrowOnSend { get; set; }

    public EmailMessage? LastMessage => Sent.Count > 0 ? Sent[^1] : null;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}
