using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>A refresh token row. <see cref="Token"/> holds the SHA-256 hash, never the raw token.</summary>
public class RefreshToken
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>The SHA-256 hash of the token.</summary>
    public string Token { get; set; } = default!;
    /// <summary>The id of the user who holds the token.</summary>
    public string UserId { get; set; } = default!;
    /// <summary>When the token expires, in UTC.</summary>
    public DateTime Expires { get; set; }
    /// <summary>When the token was issued, in UTC.</summary>
    public DateTime Created { get; set; }

    /// <summary>When a rotation used up the token, or when it was revoked after a replay.</summary>
    public DateTime? Revoked { get; set; }
}

/// <summary>A DbContext that holds refresh tokens. The app's DbContext implements it.</summary>
public interface IAuthDbContext
{
    /// <summary>The refresh token rows.</summary>
    DbSet<RefreshToken> RefreshTokens { get; }
}

/// <summary>Adds the refresh token mapping to an EF model.</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="RefreshToken"/> to the <c>RefreshTokens</c> table, with a required foreign key
    /// from <c>UserId</c> to <typeparamref name="TUser"/> that cascades on delete. EF indexes the foreign
    /// key by convention. <c>Revoked</c> is a concurrency token, which does not change the schema.
    /// </summary>
    public static ModelBuilder AddAppKitRefreshTokens<TUser>(this ModelBuilder modelBuilder) where TUser : class
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.Token).IsRequired();
            e.Property(t => t.UserId).IsRequired();
            // A save that changes a token checks that no other request revoked it in the meantime.
            e.Property(t => t.Revoked).IsConcurrencyToken();
            e.HasOne<TUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        return modelBuilder;
    }
}
