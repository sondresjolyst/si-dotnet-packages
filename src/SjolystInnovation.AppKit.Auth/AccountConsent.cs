using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>
/// One acceptance of one version of a document by one user, such as the terms. Rows are only added, never
/// updated. The age confirmation is a row too, with the document <c>age</c> and the minimum age as its version.
/// </summary>
public class AccountConsent
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>The id of the user who accepted.</summary>
    public string UserId { get; set; } = default!;
    /// <summary>The document, such as <c>terms</c> or <c>age</c>. At most 32 characters.</summary>
    public string Document { get; set; } = default!;
    /// <summary>The version the user accepted. At most 64 characters.</summary>
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
/// Says that a document must be accepted again. A user is pending for the document when a requirement is in
/// effect and the user holds no acceptance of its <see cref="Version"/>, or of the current version from
/// <see cref="IConsentVersionSource"/>, whenever it was made.
/// </summary>
public class ConsentRequirement
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>The document that must be accepted again. At most 32 characters.</summary>
    public string Document { get; set; } = default!;
    /// <summary>The version to accept. At most 64 characters.</summary>
    public string Version { get; set; } = default!;
    /// <summary>When the app marked the requirement, in UTC. The newest mark decides between requirements.</summary>
    public DateTime MarkedAt { get; set; }
    /// <summary>From when the requirement binds, in UTC. A user may accept earlier.</summary>
    public DateTime EffectiveFrom { get; set; }
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
}

/// <summary>Adds the account consent mapping to an EF model.</summary>
public static class AccountConsentModelBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="AccountConsent"/> to the <c>AccountConsents</c> table and <see cref="ConsentRequirement"/>
    /// to the <c>ConsentRequirements</c> table. A consent has a required foreign key from <c>UserId</c> to
    /// <typeparamref name="TUser"/> that cascades on delete. It is unique on user, document and version, and
    /// indexed on user, document and acceptance time. A requirement is unique on document and version. It also makes
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
            e.HasIndex(c => new { c.UserId, c.Document, c.Version }).IsUnique();
            e.HasIndex(c => new { c.UserId, c.Document, c.AcceptedAt });
            e.HasOne<TUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity<TUser>().HasIndex("NormalizedEmail").IsUnique();
        modelBuilder.Entity<ConsentRequirement>(e =>
        {
            e.ToTable("ConsentRequirements");
            e.HasKey(r => r.Id);
            e.Property(r => r.Document).IsRequired().HasMaxLength(32);
            e.Property(r => r.Version).IsRequired().HasMaxLength(64);
            e.HasIndex(r => new { r.Document, r.Version }).IsUnique();
        });
        return modelBuilder;
    }
}
