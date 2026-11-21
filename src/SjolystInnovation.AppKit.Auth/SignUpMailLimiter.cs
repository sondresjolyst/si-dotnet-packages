using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Auth;

internal static class SignUpMailKinds
{
    // Caused by anyone who types the address, so they share one budget.
    public const string Verification = "verification";
    public const string Notice = "notice";

    // Caused by the owner of the account, so they share another.
    public const string Resend = "resend";
    public const string Reminder = "reminder";

    private static readonly string[] Anonymous = [Verification, Notice];
    private static readonly string[] Owner = [Resend, Reminder];

    // The kinds that count against the same cap as the kind.
    public static string[] BudgetOf(string kind) => Array.IndexOf(Owner, kind) >= 0 ? Owner : Anonymous;
}

internal static class SignUpMailAddress
{
    // The hash of the normalised address, as Identity normalises it for lookups. A +tag in the local part does not change
    // it, so name+one@ and name+two@ count against the same address as name@.
    public static string Hash(string normalizedEmail)
    {
        var key = normalizedEmail.Trim();
        var at = key.LastIndexOf('@');
        if (at > 0)
        {
            var plus = key.IndexOf('+', StringComparison.Ordinal);
            if (plus > 0 && plus < at)
                key = string.Concat(key.AsSpan(0, plus), key.AsSpan(at));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

// Decides whether a sign-up email may go to an address now, and counts it when it may. The counts live in the database,
// so the cap and the cooldown hold across instances and restarts. The caller must not hold a transaction around the call.
internal sealed class SignUpMailLimiter<TContext>(TContext db, TimeProvider time, ILookupNormalizer? normalizer = null)
    where TContext : DbContext, IAccountConsentDbContext
{
    private const int Attempts = 3;

    // Counts one email of the kind for the address and returns true, unless the address already got the most emails of the
    // day in the budget of the kind, or the cooldown has not passed since the last email of the same kind. Nothing is counted
    // when it returns false. The key is the normalised address the account stores, or the request's address normalised the
    // way Identity does it when there is no account, so every spelling that finds the account shares one counter.
    public async Task<bool> TryReserveAsync(string email, string? storedNormalizedEmail, string kind, int maxPerDay, TimeSpan cooldown, CancellationToken cancellationToken)
    {
        var hash = SignUpMailAddress.Hash(storedNormalizedEmail ?? (normalizer?.NormalizeEmail(email.Trim()) ?? email.Trim().ToUpperInvariant()));
        var budget = SignUpMailKinds.BudgetOf(kind);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var today = now.Date;
            // Yesterday's rows are read too, so a cooldown holds across midnight.
            var rows = await db.SignUpMailLogs.Where(l => l.AddressHash == hash && l.Day >= today.AddDays(-1)).ToListAsync(cancellationToken);
            if (rows.Where(r => r.Day == today && budget.Contains(r.Kind)).Sum(r => r.Count) >= maxPerDay)
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
