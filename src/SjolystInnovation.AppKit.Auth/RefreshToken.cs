using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>A refresh token row. <see cref="Token"/> holds the SHA-256 hash, never the raw token.</summary>
public class RefreshToken
{
    public int Id { get; set; }
    public string Token { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public DateTime Expires { get; set; }
    public DateTime Created { get; set; }

    /// <summary>When the token was used up by a rotation, or revoked after a replay.</summary>
    public DateTime? Revoked { get; set; }
}

/// <summary>The app's DbContext implements this to hold refresh tokens.</summary>
public interface IAuthDbContext
{
    DbSet<RefreshToken> RefreshTokens { get; }
}

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="RefreshToken"/> to the <c>RefreshTokens</c> table, with a required foreign key
    /// from <c>UserId</c> to <typeparamref name="TUser"/> that cascades on delete. EF indexes the foreign
    /// key by convention. <c>Revoked</c> is a concurrency token, which changes no column.
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
            // Saving a change to a token checks that no other request revoked it in the meantime.
            e.Property(t => t.Revoked).IsConcurrencyToken();
            e.HasOne<TUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        return modelBuilder;
    }
}
