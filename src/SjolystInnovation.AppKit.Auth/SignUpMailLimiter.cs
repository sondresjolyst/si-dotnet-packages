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

    public static bool IsOwner(string kind) => Array.IndexOf(Owner, kind) >= 0;

    // The kinds that count against the same cap as the kind.
    public static string[] BudgetOf(string kind) => Array.IndexOf(Owner, kind) >= 0 ? Owner : Anonymous;
}

internal static class SignUpMailAddress
{
    // The hash of the normalised address exactly as it is, as Identity normalises it for lookups.
    public static string Hash(string normalizedEmail) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail.Trim())));

    // The hash of the mailbox. A +tag in the local part does not change it, so name+one@ and name+two@ are the mailbox of name@.
    public static string MailboxHash(string normalizedEmail)
    {
        var key = normalizedEmail.Trim();
        var at = key.LastIndexOf('@');
        if (at > 0)
        {
            var plus = key.IndexOf('+', StringComparison.Ordinal);
            if (plus > 0 && plus < at)
                key = string.Concat(key.AsSpan(0, plus), key.AsSpan(at));
        }
        return Hash(key);
    }
}

// Decides whether a sign-up email may go to an address now, and counts it when it may. The counts live in the database,
// so the cap and the cooldown hold across instances and restarts. The caller must not hold a transaction around the call.
internal sealed class SignUpMailLimiter<TContext>(TContext db, TimeProvider time, ILookupNormalizer? normalizer = null)
    where TContext : DbContext, IAccountConsentDbContext
{
    private const int Attempts = 3;

    // Counts one email of the kind for the address and returns true only when every rule that applies allows it. Nothing is
    // counted when it returns false. The anonymous kinds count per mailbox against the anonymous cap. The owner kinds count
    // per account, by its exact normalised address, against the owner cap. Over every kind and every account, a mailbox gets
    // at most the two caps together per day. A cooldown holds between emails of the same kind. The key is the normalised
    // address the account stores, or the request's address normalised the way Identity does it when there is no account,
    // so every spelling that finds the account shares its counters.
    public async Task<bool> TryReserveAsync(string email, string? storedNormalizedEmail, string kind, int anonymousCap, int ownerCap, TimeSpan cooldown, CancellationToken cancellationToken)
    {
        var exact = storedNormalizedEmail ?? (normalizer?.NormalizeEmail(email.Trim()) ?? email.Trim().ToUpperInvariant());
        var mailbox = SignUpMailAddress.MailboxHash(exact);
        var isOwner = SignUpMailKinds.IsOwner(kind);
        var hash = isOwner ? SignUpMailAddress.Hash(exact) : mailbox;
        var budget = SignUpMailKinds.BudgetOf(kind);
        var cap = isOwner ? ownerCap : anonymousCap;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var today = now.Date;
            // Yesterday's rows are read too, so a cooldown holds across midnight.
            var rows = await db.SignUpMailLogs.Where(l => l.MailboxHash == mailbox && l.Day >= today.AddDays(-1)).ToListAsync(cancellationToken);
            var todays = rows.Where(r => r.Day == today).ToList();
            if (todays.Sum(r => r.Count) >= anonymousCap + ownerCap)
                return false;
            if (todays.Where(r => r.AddressHash == hash && budget.Contains(r.Kind)).Sum(r => r.Count) >= cap)
                return false;
            if (cooldown > TimeSpan.Zero && rows.Any(r => r.AddressHash == hash && r.Kind == kind && now - r.LastSentAt < cooldown))
                return false;

            var row = rows.FirstOrDefault(r => r.AddressHash == hash && r.Kind == kind && r.Day == today);
            if (row is null)
                db.SignUpMailLogs.Add(row = new SignUpMailLog { AddressHash = hash, MailboxHash = mailbox, Kind = kind, Day = today, Count = 1, LastSentAt = now });
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
