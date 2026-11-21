using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>
/// One acceptance of one version of a document by one user, such as the terms. Rows are only added, never
/// updated. The age confirmation is a row too, with the document <c>age</c> and the minimum age as its version.
/// </summary>
public class AccountConsent
{
    /// <summary>
    /// The version that is stored for the terms when the app has no <see cref="IConsentVersionSource"/>, or the source gives
    /// no version for the document. The version the client sent is not stored then. It is also the version a requirement
    /// records then. Its value is <c>unversioned</c>.
    /// </summary>
    public const string Unversioned = "unversioned";

    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>The id of the user who accepted.</summary>
    public string UserId { get; set; } = default!;
    /// <summary>The document, such as <c>terms</c> or <c>age</c>. At most 32 characters.</summary>
    public string Document { get; set; } = default!;
    /// <summary>The version the user accepted, as the source served it. At most 64 characters.</summary>
    public string Version { get; set; } = default!;
    /// <summary>The locale the user saw the document in, or null. At most 16 characters.</summary>
    public string? Locale { get; set; }
    /// <summary>When the user accepted, in UTC.</summary>
    public DateTime AcceptedAt { get; set; }
    /// <summary>How the acceptance came about, such as <c>register</c>, <c>reaccept</c> or <c>migration</c>. At most 16 characters.</summary>
    public string Source { get; set; } = default!;
    /// <summary>The shortened IP address of the request, or null when the app does not record it. At most 45 characters.</summary>
    public string? Ip { get; set; }
}

/// <summary>
/// Says that a document must be accepted again from a moment on. Every requirement of a document whose <see cref="EffectiveFrom"/>
/// is not after now is in effect, and the latest <see cref="MarkedAt"/> among them decides. A user is pending for the document when
/// at least one requirement is in effect and the user's latest acceptance of the document, of any version, was made before that
/// mark, or the user has none. A requirement that was marked earlier but takes effect later never replaces a newer mark.
/// </summary>
public class ConsentRequirement
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>The document that must be accepted again. At most 32 characters.</summary>
    public string Document { get; set; } = default!;
    /// <summary>
    /// The version that <see cref="IConsentVersionSource"/> served when the requirement was marked, or
    /// <see cref="AccountConsent.Unversioned"/> when it served none. A record only, it is never compared. At most 64 characters.
    /// </summary>
    public string Version { get; set; } = default!;
    /// <summary>When the app marked the requirement, in UTC. An acceptance at or after it meets the requirement.</summary>
    public DateTime MarkedAt { get; set; }
    /// <summary>From when the requirement binds, in UTC. An acceptance made after the mark and before this time meets it.</summary>
    public DateTime EffectiveFrom { get; set; }
}

/// <summary>
/// Counts the sign-up emails sent to one address on one day, by kind. The address is kept as an HMAC-SHA256 hash of its
/// normalised form, folded to its mailbox key (see the sign-up options), never as the address itself. The key of the hash
/// is derived from <see cref="AppKitAuthOptions.Key"/>, so a copy of the table cannot be reversed by hashing guessed
/// addresses, and changing the signing key resets the counts. The sign-up reads it to cap the emails per address and to
/// space out the notices to an existing account.
/// </summary>
public class SignUpMailLog
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>
    /// The hash that the count belongs to, as 64 lowercase hex characters. It is the mailbox for the emails that anyone
    /// can cause, and the exact normalised address of the account for the emails that its owner causes.
    /// </summary>
    public string AddressHash { get; set; } = default!;
    /// <summary>The hash of the mailbox key of the normalised address, as 64 lowercase hex characters.</summary>
    public string MailboxHash { get; set; } = default!;
    /// <summary>
    /// The kind of email, <c>verification</c>, <c>notice</c>, <c>resend</c> or <c>reminder</c>, or the name of a running count,
    /// <c>total</c>, <c>anonymous</c> or <c>owner</c>. At most 16 characters.
    /// </summary>
    public string Kind { get; set; } = default!;
    /// <summary>The UTC day of the emails, at midnight.</summary>
    public DateTime Day { get; set; }
    /// <summary>How many emails of the kind were reserved for the address on the day.</summary>
    public int Count { get; set; }
    /// <summary>When the last email of the kind was reserved, in UTC.</summary>
    public DateTime LastSentAt { get; set; }
}

/// <summary>A document the user must accept again, with the version that is current.</summary>
/// <param name="Document">The document, such as <c>terms</c>.</param>
/// <param name="Version">The version to accept.</param>
public sealed record PendingConsent(string Document, string Version);

/// <summary>A DbContext that holds account consents. The app's DbContext implements it.</summary>
public interface IAccountConsentDbContext
{
    /// <summary>The account consent rows.</summary>
    DbSet<AccountConsent> AccountConsents { get; }

    /// <summary>The consent requirement rows.</summary>
    DbSet<ConsentRequirement> ConsentRequirements { get; }

    /// <summary>The sign-up email count rows.</summary>
    DbSet<SignUpMailLog> SignUpMailLogs { get; }
}

/// <summary>Adds the account consent mapping to an EF model.</summary>
public static class AccountConsentModelBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="AccountConsent"/> to the <c>AccountConsents</c> table and <see cref="ConsentRequirement"/>
    /// to the <c>ConsentRequirements</c> table. A consent has a required foreign key from <c>UserId</c> to
    /// <typeparamref name="TUser"/> that cascades on delete. It is indexed on user, document and acceptance
    /// time, and is not unique. A requirement is unique on document and effective time. <see cref="SignUpMailLog"/> is mapped to the
    /// <c>SignUpMailLogs</c> table, unique on address hash, kind and day. It also makes
    /// the index on the normalised email of <typeparamref name="TUser"/> unique, so the database refuses a second
    /// account for one email even when two sign-ups race. A database that already holds two accounts for one email
    /// must be cleaned up before the migration that adds it.
    /// </summary>
    public static ModelBuilder AddAppKitAccountConsents<TUser>(this ModelBuilder modelBuilder) where TUser : class
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AccountConsent>(e =>
        {
            e.ToTable("AccountConsents");
            e.HasKey(c => c.Id);
            e.Property(c => c.UserId).IsRequired();
            e.Property(c => c.Document).IsRequired().HasMaxLength(32);
            e.Property(c => c.Version).IsRequired().HasMaxLength(64);
            e.Property(c => c.Locale).HasMaxLength(16);
            e.Property(c => c.Source).IsRequired().HasMaxLength(16);
            e.Property(c => c.Ip).HasMaxLength(45);
            e.HasIndex(c => new { c.UserId, c.Document, c.AcceptedAt });
            e.HasOne<TUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity<SignUpMailLog>(e =>
        {
            e.ToTable("SignUpMailLogs");
            e.HasKey(l => l.Id);
            e.Property(l => l.AddressHash).IsRequired().HasMaxLength(64);
            e.Property(l => l.MailboxHash).IsRequired().HasMaxLength(64);
            e.Property(l => l.Kind).IsRequired().HasMaxLength(16);
            e.Property(l => l.Count).IsConcurrencyToken();
            e.HasIndex(l => new { l.AddressHash, l.Kind, l.Day }).IsUnique();
            e.HasIndex(l => new { l.MailboxHash, l.Day });
            e.HasIndex(l => l.Day);
        });
        modelBuilder.Entity<TUser>().HasIndex("NormalizedEmail").IsUnique();
        modelBuilder.Entity<ConsentRequirement>(e =>
        {
            e.ToTable("ConsentRequirements");
            e.HasKey(r => r.Id);
            e.Property(r => r.Document).IsRequired().HasMaxLength(32);
            e.Property(r => r.Version).IsRequired().HasMaxLength(64);
            e.HasIndex(r => new { r.Document, r.EffectiveFrom }).IsUnique();
        });
        return modelBuilder;
    }
}
