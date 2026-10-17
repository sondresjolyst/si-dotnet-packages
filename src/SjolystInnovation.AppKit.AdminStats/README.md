# SjolystInnovation.AppKit.AdminStats

This package keeps the daily history behind an admin dashboard chart, such as the number of users
over time. It stores one row of counts per completed UTC day in your app's own snapshot table, and it
answers the stored days with a live row for today. Today is never stored, so a sign-up or a deletion
later in the day still shows. Use it when your dashboard needs a history that your data alone cannot
give, or that is too slow to count on every request.

```sh
dotnet add package SjolystInnovation.AppKit.AdminStats
```

## Get started

Your snapshot entity implements `IAppKitDailyStat`, holds your own `int` or `long` counters, and keeps
a unique index on `Date`. The history's JSON keys come from the property names, so your frontend keeps
its keys as long as the entity keeps its names. An existing table with a `Date` column and a unique
index on it fits without a migration.

```csharp
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.AdminStats;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAuthentication();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"));

builder.Services.AddAppKitDailyStats<ApplicationDbContext, DailyStatSnapshot>(o =>
{
    o.LiveSampler = async (db, ct) => new DailyStatSnapshot
    {
        TotalUsers = await db.Users.CountAsync(u => !u.IsDeleted, ct),
    };
    o.AsOfSampler = async (db, day, ct) =>
    {
        var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new DailyStatSnapshot
        {
            TotalUsers = await db.Users.CountAsync(u => u.CreatedAt < end && !(u.IsDeleted && u.DeletedAt < end), ct),
        };
    };
    o.EarliestDay = async (db, ct) =>
        await db.Users.MinAsync(u => (DateTime?)u.CreatedAt, ct) is { } first ? DateOnly.FromDateTime(first) : null;
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapAppKitDailyStats<ApplicationDbContext, DailyStatSnapshot>("/api/admin/stats");

app.Run();

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<DailyStatSnapshot> DailyStatSnapshots => Set<DailyStatSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DailyStatSnapshot>().HasIndex(s => s.Date).IsUnique();
}

public class User
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class DailyStatSnapshot : IAppKitDailyStat
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int TotalUsers { get; set; }
}
```

The day bounds must have `DateTimeKind.Utc` when the columns are `timestamptz`, because Npgsql refuses
an unspecified kind.

| Call | What it does |
| --- | --- |
| `AddAppKitDailyStats<TContext, TSnapshot>` | Registers `AppKitDailyStats<TContext, TSnapshot>` and the hosted service that stores missed days. |
| `MapAppKitDailyStats<TContext, TSnapshot>` | Maps the history endpoint. |

| Option | Default | Meaning |
| --- | --- | --- |
| `LiveSampler` | Required | The counts right now. It returns a new snapshot each time, and the package sets `Date`. |
| `AsOfSampler` | None | The counts at the end of a past UTC day. See the missed days section. |
| `BackfillFrom` | None | The first day to store when the table is empty. It applies only with `AsOfSampler`, and it wins over `EarliestDay`. |
| `EarliestDay` | None | The earliest day with any data, to store from when the table is empty. If it returns `null`, nothing has happened yet and nothing is stored. It applies only with `AsOfSampler`. |
| `MaxBackfillDays` | 400 | The most days one run stores. Older missed days are left out, which also guards against a stray timestamp such as `0001-01-01`. |
| `Interval` | 6 hours | The longest wait between runs. At most about 49 days. |

The app fails to start without a `LiveSampler`, with a `MaxBackfillDays` below 1, or with an
`Interval` that is not positive or is over the maximum.

## Missed days

A hosted service stores the missed completed days when the app starts, one minute after each UTC
midnight, and at least every `Interval`. It continues from the latest stored row through yesterday.
It never changes a stored row.

- With `AsOfSampler`, each missed day gets its own counts. On the first run it starts at
  `BackfillFrom`, else at `EarliestDay`, else at yesterday. The first backfill calls `AsOfSampler` once
  per day, one after the other, up to `MaxBackfillDays` calls, on every replica that starts with an
  empty table.
- A counter that your data cannot rebuild, such as hard-deleted rows, still needs a value from
  `AsOfSampler`. Return the live count for it. The package has no way to mix samplers per counter.
- Without `AsOfSampler`, every missed day gets the counts at the time the run happens, not at the end
  of that day. The first run stores only yesterday. If the app was down for several days, those days
  show as a flat line at the level of the run.

If the UTC day changes while a run is going, the service runs again one minute after that midnight.
If a run fails, the service logs the error and tries again after one minute, then after twice as long
each time, up to `Interval`.

Two writers can store the same day at once, such as two replicas, or your own code that calls
`AppKitDailyStats<TContext, TSnapshot>.FreezeMissingDaysAsync`. The unique index on `Date` stops the
second writer, which skips that day instead of failing. `FreezeMissingDaysAsync` saves the context you
pass, so pass one without other pending changes and without an open transaction. A duplicate day
fails its insert, and in PostgreSQL that aborts the open transaction. The hosted service uses a scope
of its own.

## Endpoint

`MapAppKitDailyStats` maps one route under its prefix, which defaults to `/api/admin/stats`:

| Route | Who | What it does |
| --- | --- | --- |
| `GET history?from=` | `Policy` | Answers the stored days and a live row for today, oldest first. |

The answer is `[{ "date": "yyyy-MM-dd", ...counters }]`, which `StatHistoryChart` in the
`@sjolystinnovation/app-kit` npm package reads. The counters are the snapshot's public `int` and
`long` properties in camel case, apart from `Id`. They need a public getter and setter. Other types,
such as `int?`, `short`, `decimal`, and `bool`, and computed properties are left out.

`from` is an optional `yyyy-MM-dd` date that sets the first day. A malformed date answers `400`. When
nothing is stored and every count today is zero, the answer is an empty list, so a new app shows no
lone zero row.

The endpoint only reads. It does not store missed days, because the hosted service runs shortly after
midnight. `AppKitDailyStats<TContext, TSnapshot>.GetHistoryAsync` gives the same rows to your own code.

| Option | Default | Meaning |
| --- | --- | --- |
| `Policy` | `Admin` | The authorization policy that the history requires. A null or blank policy throws when you map the endpoint. |

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.AdminStats)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.AdminStats/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)
