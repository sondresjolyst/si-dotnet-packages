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
public sealed record EmailAttachment(string Name, byte[] Content);

/// <summary>The result of sending an email, including whether the provider accepted it for delivery.</summary>
/// <param name="Sent">Whether the provider accepted the email.</param>
/// <param name="MessageId">The provider's id for an accepted email.</param>
/// <param name="Error">The reason the email was not accepted, as the provider or the connection reported it.</param>
public sealed record EmailResult(bool Sent, string? MessageId = null, string? Error = null);

/// <summary>The delivery counts over the last days, including today.</summary>
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
    /// <summary>Gets the counts for the last <paramref name="days"/> days. Throws when <paramref name="days"/> is below 1 or the provider cannot be read.</summary>
    Task<EmailStats> GetAsync(int days = 30, CancellationToken cancellationToken = default);
}
