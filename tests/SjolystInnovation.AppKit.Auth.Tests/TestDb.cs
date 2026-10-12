using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class TestUser : IdentityUser, IPasswordResetUser, IEmailVerificationUser
{
    public string? PasswordResetCodeHash { get; set; }
    public DateTime? PasswordResetCodeExpiration { get; set; }
    public int PasswordResetAttempts { get; set; }
    public string? EmailVerificationCodeHash { get; set; }
    public DateTime? EmailVerificationCodeExpiration { get; set; }
    public int EmailVerificationAttempts { get; set; }
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IAuthDbContext
{
    public DbSet<TestUser> Users => Set<TestUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddAppKitRefreshTokens<TestUser>();
}

/// <summary>A SQLite database in memory, so transactions and foreign keys are real.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    /// <summary>A fresh context on the same database, like a new request.</summary>
    public TestDbContext NewContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    public void Dispose() => _connection.Dispose();
}
