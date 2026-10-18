namespace SjolystInnovation.AppKit.Email;

/// <summary>One transactional email to one recipient.</summary>
/// <param name="To">The recipient's address.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="HtmlBody">The whole HTML body.</param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody)
{
    /// <summary>The address replies go to instead of the sender, for example the person who filled in a contact form.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>The files sent with the email. Defaults to none.</summary>
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

/// <summary>A file sent with an email. The provider takes its type from the file name's extension.</summary>
/// <param name="Name">The file name, such as <c>invoice-0007.pdf</c>. Its extension sets the type.</param>
/// <param name="Content">The file's bytes.</param>
public sealed record EmailAttachment(string Name, byte[] Content);

/// <summary>The result of sending an email, including whether the provider accepted it for delivery.</summary>
/// <param name="Sent">Whether the provider accepted the email.</param>
/// <param name="MessageId">The provider's id for an accepted email.</param>
/// <param name="Error">The reason the email was not accepted, as the provider or the connection reported it.</param>
public sealed record EmailResult(bool Sent, string? MessageId = null, string? Error = null);

/// <summary>The delivery counts over the last days, including today.</summary>
/// <param name="Days">The number of days the counts cover.</param>
/// <param name="Requests">The emails sent to the provider.</param>
/// <param name="Delivered">The emails delivered.</param>
/// <param name="HardBounces">The emails refused for good, such as to an address that does not exist.</param>
/// <param name="SoftBounces">The emails refused for now, such as to a full mailbox.</param>
/// <param name="SpamReports">The emails that recipients marked as spam.</param>
/// <param name="Blocked">The emails the provider did not send, such as to an address that bounced before.</param>
/// <param name="Invalid">The emails to an address that is not valid.</param>
public sealed record EmailStats(int Days, int Requests, int Delivered, int HardBounces, int SoftBounces, int SpamReports, int Blocked, int Invalid);

/// <summary>Sends transactional email.</summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends <paramref name="message"/>. A refusal or a failed connection does not throw. The sender logs
    /// it and returns a result with <see cref="EmailResult.Sent"/> set to false. Only cancellation and a
    /// message without a recipient throw.
    /// </summary>
    Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Reads delivery statistics.</summary>
public interface IEmailStats
{
    /// <summary>The most days the provider reports on. Defaults to <see cref="int.MaxValue"/>, no limit.</summary>
    int MaxDays => int.MaxValue;

    /// <summary>
    /// Gets the counts for the last <paramref name="days"/> days. Throws when <paramref name="days"/> is below 1
    /// or above <see cref="MaxDays"/>, or the provider cannot be read.
    /// </summary>
    Task<EmailStats> GetAsync(int days = 30, CancellationToken cancellationToken = default);
}
