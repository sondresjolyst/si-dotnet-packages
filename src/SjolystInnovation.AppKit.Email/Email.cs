namespace SjolystInnovation.AppKit.Email;

/// <summary>One transactional email to one recipient.</summary>
/// <param name="To">The recipient's address.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="HtmlBody">The whole HTML body.</param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody)
{
    /// <summary>Where replies go instead of the sender, such as the person who filled in a contact form.</summary>
    public string? ReplyTo { get; init; }

    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

/// <summary>A file sent with an email. Its type is taken from the file name's extension.</summary>
public sealed record EmailAttachment(string Name, byte[] Content);

/// <summary>Whether the email was accepted for delivery.</summary>
/// <param name="Sent">True when the provider accepted it.</param>
/// <param name="MessageId">The provider's id for an accepted email.</param>
/// <param name="Error">Why it was not accepted, as the provider or the connection put it.</param>
public sealed record EmailResult(bool Sent, string? MessageId = null, string? Error = null);

/// <summary>Delivery counts over the last days, today included.</summary>
public sealed record EmailStats(int Days, int Requests, int Delivered, int HardBounces, int SoftBounces, int SpamReports, int Blocked, int Invalid);

/// <summary>Sends transactional email.</summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends <paramref name="message"/>. A refusal or a failed connection does not throw: it is logged
    /// and comes back as a result with <see cref="EmailResult.Sent"/> false. Only cancelling throws.
    /// </summary>
    Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Reads delivery statistics.</summary>
public interface IEmailStats
{
    /// <summary>The counts for the last <paramref name="days"/> days. Throws when the provider cannot be read.</summary>
    Task<EmailStats> GetAsync(int days = 30, CancellationToken cancellationToken = default);
}
