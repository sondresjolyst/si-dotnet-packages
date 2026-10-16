# SjolystInnovation.AppKit.AdminStats

This package keeps the daily history behind an admin dashboard chart. It stores one row of counts per
completed UTC day in your app's own snapshot table, and it answers the stored days with a live row for
today. Today is never stored, so a sign-up or a deletion later in the day still shows.

```sh
dotnet add package SjolystInnovation.AppKit.AdminStats
```

## Setup

Your snapshot entity implements `IAppKitDailyStat`, holds your own `int` or `long` counters, and keeps a
unique index on `Date`. The apps' existing `DailyStatSnapshots` tables already fit, so no migration is
needed. The history's JSON keys come from the property names, so your frontend keeps its keys as long as
the entity keeps its names.

```csharp
public class DailyStatSnapshot : IAppKitDailyStat
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int TotalUsers { get; set; }
}

modelBuilder.Entity<DailyStatSnapshot>().HasIndex(s => s.Date).IsUnique();

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

app.MapAppKitDailyStats<ApplicationDbContext, DailyStatSnapshot>("/api/admin/stats", o => o.Policy = Policies.Admin);
```

The day bounds must have `DateTimeKind.Utc` when the columns are `timestamptz`, because Npgsql refuses an
unspecified kind.

| Option | Default | |
| --- | --- | --- |
| `LiveSampler` | Required | The counts right now. It returns a new snapshot each time, and the package sets `Date`. |
| `AsOfSampler` | None | The counts at the end of a past UTC day. See the next section. |
| `BackfillFrom` | None | The first day to store when the table is empty. It applies only with `AsOfSampler`. |
| `EarliestDay` | None | The earliest day with any data, to store from when the table is empty. If it returns `null`, nothing has happened yet and nothing is stored. It applies only with `AsOfSampler`. |
| `MaxBackfillDays` | 400 | The most days one run stores. Older missed days are left out, which also guards against a stray timestamp such as `0001-01-01`. |
| `Interval` | 6 hours | The longest wait between runs. At most about 49 days. |

## Missed days

A hosted service stores the missed completed days when the app starts, one minute after each UTC
midnight, and at least every `Interval`. It continues from the latest stored row through yesterday. It
never changes a stored row.

- With `AsOfSampler`, each missed day gets its own counts. On the first run it starts at `BackfillFrom`,
  else at `EarliestDay`, else at yesterday. The first backfill calls `AsOfSampler` once per day, one after
  the other, up to `MaxBackfillDays` calls, on every replica that starts with an empty table.
- A counter that your data cannot rebuild, such as hard-deleted rows, still needs a value from
  `AsOfSampler`. Return the live count for it. The package has no way to mix samplers per counter.
- Without `AsOfSampler`, every missed day gets the counts at the time the run happens, not at the end of
  that day. The first run stores only yesterday. If the app was down for several days, those days show as
  a flat line at the level of the run.

If the UTC day changes while a run is going, the service runs again one minute after that midnight. If a
run fails, the service logs the error and tries again after one minute, then after twice as long each
time, up to `Interval`.

Two writers can store the same day at once, such as two replicas, or your own code that calls
`AppKitDailyStats<TContext, TSnapshot>.FreezeMissingDaysAsync`. The unique index on `Date` stops the
second writer, which skips that day instead of failing. `FreezeMissingDaysAsync` saves the context you
pass, so pass one without other pending changes and without an open transaction. A duplicate day fails its
insert, and in PostgreSQL that aborts the open transaction. The hosted service uses a scope of its own.

## Endpoint

| Route | Who | Answer |
| --- | --- | --- |
| `GET history?from=` | `Policy` | The stored days and a live row for today, oldest first. |

The answer is `[{ "date": "yyyy-MM-dd", ...counters }]`, which `StatHistoryChart` in
`@sjolystinnovation/app-kit` reads. The counters are the snapshot's public `int` and `long` properties in
camel case, apart from `Id`. They need a public getter and setter. Other types, such as `int?`, `short`,
`decimal`, and `bool`, and computed properties are left out. `from` is an optional `yyyy-MM-dd` date that sets the first day. A malformed
date answers `400`. When nothing is stored and every count today is zero, the answer is an empty list, so a
new app shows no lone zero row.

The endpoint only reads. It does not store missed days, because the hosted service runs just after
midnight. `AppKitDailyStats<TContext, TSnapshot>.GetHistoryAsync` gives the same rows to your own code.

| Option | Default | |
| --- | --- | --- |
| `Policy` | `Admin` | What the history requires. A null or blank policy throws when you map the endpoint. |
