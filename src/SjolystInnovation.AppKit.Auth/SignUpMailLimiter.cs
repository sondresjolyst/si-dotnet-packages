using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Auth;

internal static class SignUpMailKinds
{
    public const string Verification = "verification";
    public const string Notice = "notice";
    public const string Reminder = "reminder";
}

internal static class SignUpMailAddress
{
    // The hash of the address as a mailbox. Case and a +tag in the local part do not change it, so name+one@ and name+two@
    // count against the same address.
    public static string Hash(string email)
    {
        var normal = email.Trim().ToLowerInvariant();
        var at = normal.LastIndexOf('@');
        if (at > 0)
        {
            var plus = normal.IndexOf('+', StringComparison.Ordinal);
            if (plus > 0 && plus < at)
                normal = string.Concat(normal.AsSpan(0, plus), normal.AsSpan(at));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normal)));
    }
}

// Decides whether a sign-up email may go to an address now, and counts it when it may. The counts live in the database,
// so the cap and the cooldown hold across instances and restarts. The caller must not hold a transaction around the call.
internal sealed class SignUpMailLimiter<TContext>(TContext db, TimeProvider time)
    where TContext : DbContext, IAccountConsentDbContext
{
    private const int Attempts = 3;

    // Counts one email of the kind for the address and returns true, unless the address already got the most emails of the
    // day, or the cooldown has not passed since the last email of the same kind. Nothing is counted when it returns false.
    public async Task<bool> TryReserveAsync(string email, string kind, int maxPerDay, TimeSpan cooldown, CancellationToken cancellationToken)
    {
        var hash = SignUpMailAddress.Hash(email);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var today = now.Date;
            // Yesterday's rows are read too, so a cooldown holds across midnight.
            var rows = await db.SignUpMailLogs.Where(l => l.AddressHash == hash && l.Day >= today.AddDays(-1)).ToListAsync(cancellationToken);
            if (rows.Where(r => r.Day == today).Sum(r => r.Count) >= maxPerDay)
                return false;
            if (cooldown > TimeSpan.Zero && rows.Any(r => r.Kind == kind && now - r.LastSentAt < cooldown))
                return false;

            var row = rows.FirstOrDefault(r => r.Kind == kind && r.Day == today);
            if (row is null)
                db.SignUpMailLogs.Add(row = new SignUpMailLog { AddressHash = hash, Kind = kind, Day = today, Count = 1, LastSentAt = now });
            else
            {
                row.Count++;
                row.LastSentAt = now;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                // Another request counted the same address first. Read the counts again.
                foreach (var tracked in rows.Append(row))
                    db.Entry(tracked).State = EntityState.Detached;
            }
        }
        return false;
    }

    // Removes the rows that no cooldown or cap can read any more, those from before yesterday.
    public async Task RemoveOldAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.Date.AddDays(-1);
        db.SignUpMailLogs.RemoveRange(await db.SignUpMailLogs.Where(l => l.Day < cutoff).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }
}
