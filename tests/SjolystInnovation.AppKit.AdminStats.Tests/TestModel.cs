using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace SjolystInnovation.AppKit.AdminStats.Tests;

/// <summary>A user that is soft-deleted, as in the apps.</summary>
public class User
{
    public required string Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

/// <summary>A sensor that is hard-deleted, as in garge.</summary>
public class Sensor
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>garge's snapshot entity.</summary>
public class DailyStatSnapshot : IAppKitDailyStat
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int TotalUsers { get; set; }
    public int TotalSensors { get; set; }
}

/// <summary>A snapshot with a <c>long</c> counter and properties that are not counters.</summary>
public class ShapeSnapshot : IAppKitDailyStat
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int TotalUsers { get; set; }
    public long StorageBytes { get; set; }
    public string? Note { get; set; }
    public int? Optional { get; set; }
    public short Small { get; set; }
    public bool Flag { get; set; }
    public decimal Amount { get; set; }
    public int Doubled => TotalUsers * 2;
    public long Computed { get => StorageBytes; private set => StorageBytes = value; }
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<DailyStatSnapshot> DailyStatSnapshots => Set<DailyStatSnapshot>();
    public DbSet<ShapeSnapshot> ShapeSnapshots => Set<ShapeSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DailyStatSnapshot>().HasIndex(s => s.Date).IsUnique();
        modelBuilder.Entity<ShapeSnapshot>().HasIndex(s => s.Date).IsUnique();
    }
}

/// <summary>The samplers garge would pass, over users and sensors.</summary>
public static class GargeSamplers
{
    public static async Task<DailyStatSnapshot> LiveAsync(TestDbContext db, CancellationToken ct) => new()
    {
        TotalUsers = await db.Users.CountAsync(u => !u.IsDeleted, ct),
        TotalSensors = await db.Sensors.CountAsync(ct),
    };

    /// <summary>
    /// Counts users who signed up and were not deleted by the end of the day. Sensors leave no trace when
    /// deleted, so older days count the ones created by then, and yesterday takes the live count.
    /// </summary>
    public static Func<TestDbContext, DateOnly, CancellationToken, Task<DailyStatSnapshot>> AsOf(TimeProvider time) => async (db, day, ct) =>
    {
        var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var yesterday = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        return new DailyStatSnapshot
        {
            TotalUsers = await db.Users.CountAsync(u => u.CreatedAt < end && !(u.IsDeleted && u.DeletedAt < end), ct),
            TotalSensors = day == yesterday ? await db.Sensors.CountAsync(ct) : await db.Sensors.CountAsync(s => s.CreatedAt < end, ct),
        };
    };

    public static async Task<DateOnly?> EarliestAsync(TestDbContext db, CancellationToken ct)
    {
        var user = await db.Users.Select(u => (DateTime?)u.CreatedAt).OrderBy(d => d).FirstOrDefaultAsync(ct);
        var sensor = await db.Sensors.Select(s => (DateTime?)s.CreatedAt).OrderBy(d => d).FirstOrDefaultAsync(ct);
        var earliest = new[] { user, sensor }.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty().Min();
        return earliest == default ? null : DateOnly.FromDateTime(earliest);
    }
}

/// <summary>SQLite in memory with a fixed clock at noon UTC on 16 October 2026.</summary>
public sealed class TestDb : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private string? _file;

    public FakeTimeProvider Time { get; } = new(Now);

    private TestDb() { }

    /// <summary>Creates the database in memory, or in a temporary file when contexts must use it from several threads at once.</summary>
    public static async Task<TestDb> CreateAsync(bool file = false)
    {
        var db = new TestDb();
        if (file)
            db._file = Path.Combine(Path.GetTempPath(), $"appkit-adminstats-{Guid.NewGuid():N}.db");
        else
            await db._connection.OpenAsync();
        await using var context = db.NewContext();
        await context.Database.EnsureCreatedAsync();
        return db;
    }

    public TestDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        if (_file is null)
            builder.UseSqlite((DbConnection)_connection);
        else
            builder.UseSqlite($"Data Source={_file};Pooling=False");
        return new(builder.AddInterceptors(interceptors).Options);
    }

    public AppKitDailyStats<TestDbContext, TSnapshot> Stats<TSnapshot>(Action<AppKitDailyStatsOptions<TestDbContext, TSnapshot>> configure)
        where TSnapshot : class, IAppKitDailyStat
    {
        var options = new AppKitDailyStatsOptions<TestDbContext, TSnapshot>();
        configure(options);
        return new AppKitDailyStats<TestDbContext, TSnapshot>(Options.Create(options), Time);
    }

    /// <summary>The service as garge would set it up, with the past-day sampler and the earliest day.</summary>
    public AppKitDailyStats<TestDbContext, DailyStatSnapshot> Garge() => Stats<DailyStatSnapshot>(o =>
    {
        o.LiveSampler = GargeSamplers.LiveAsync;
        o.AsOfSampler = GargeSamplers.AsOf(Time);
        o.EarliestDay = GargeSamplers.EarliestAsync;
    });

    /// <summary>The service as the other three apps set it up today, with the live sampler only.</summary>
    public AppKitDailyStats<TestDbContext, DailyStatSnapshot> LiveOnly() => Stats<DailyStatSnapshot>(o => o.LiveSampler = GargeSamplers.LiveAsync);

    public static User MakeUser(string id, DateOnly created, DateOnly? deleted = null) => new()
    {
        Id = id,
        CreatedAt = created.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
        IsDeleted = deleted is not null,
        DeletedAt = deleted?.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
    };

    public static Sensor MakeSensor(int id, DateOnly created) => new() { Id = id, CreatedAt = created.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc) };

    public async Task AddAsync(params object[] entities)
    {
        await using var context = NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    public async Task<List<DailyStatSnapshot>> StoredAsync()
    {
        await using var context = NewContext();
        return await context.DailyStatSnapshots.AsNoTracking().OrderBy(s => s.Date).ToListAsync();
    }

    public async Task<DailyStatSnapshot?> FrozenAsync(DateOnly date) => (await StoredAsync()).SingleOrDefault(s => s.Date == date);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (_file is not null && File.Exists(_file))
            File.Delete(_file);
    }
}
