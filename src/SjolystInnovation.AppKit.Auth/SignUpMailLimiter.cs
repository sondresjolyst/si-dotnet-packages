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

    // Rows that hold a running count for a rule, not the emails of one kind.
    public const string Total = "total";
    public const string AnonymousBudget = "anonymous";
    public const string OwnerBudget = "owner";

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
// so the caps and the cooldown hold across instances and restarts. The caller must not hold a transaction around the call,
// and sends the email only after this returns true, which is after the counts are saved.
//
// Three rules hold, each with its own running count row per day. The anonymous count per mailbox, the owner count per
// account, and the total per mailbox over every kind. A row per kind also keeps the time of the last email for the cooldown.
// One reservation raises its rows in one SaveChanges. Every count is an EF Core concurrency token, so when two requests
// raise the same row from the same value, one save fails with a concurrency conflict. A new row can lose to the unique index
// on hash, kind and day. Either way the rows are read again and the reservation tried again, a fixed number of times, and
// it fails closed after that. The mechanism is optimistic concurrency, so it needs no raw SQL and no isolation level.
internal sealed class SignUpMailLimiter<TContext>(TContext db, TimeProvider time, ILookupNormalizer? normalizer = null)
    where TContext : DbContext, IAccountConsentDbContext
{
    // Every failed attempt means another reservation was saved in between. More attempts than the largest cap in a burst
    // would never be needed, and a few more than a usual cap are enough.
    private const int Attempts = 8;

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
        var budgetKind = isOwner ? SignUpMailKinds.OwnerBudget : SignUpMailKinds.AnonymousBudget;
        var cap = isOwner ? ownerCap : anonymousCap;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var today = now.Date;
            // Yesterday's rows are read too, so a cooldown holds across midnight.
            var rows = await db.SignUpMailLogs.Where(l => l.MailboxHash == mailbox && l.Day >= today.AddDays(-1)).ToListAsync(cancellationToken);
            SignUpMailLog? Find(string rowHash, string rowKind) => rows.FirstOrDefault(r => r.AddressHash == rowHash && r.Kind == rowKind && r.Day == today);

            var total = Find(mailbox, SignUpMailKinds.Total);
            var budget = Find(hash, budgetKind);
            if ((total?.Count ?? 0) >= anonymousCap + ownerCap || (budget?.Count ?? 0) >= cap)
                return false;
            if (cooldown > TimeSpan.Zero && rows.Any(r => r.AddressHash == hash && r.Kind == kind && now - r.LastSentAt < cooldown))
                return false;

            var raised = new List<SignUpMailLog>
            {
                Raise(total, mailbox, mailbox, SignUpMailKinds.Total, today, now),
                Raise(budget, hash, mailbox, budgetKind, today, now),
                Raise(Find(hash, kind), hash, mailbox, kind, today, now),
            };
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                // A concurrency conflict or a lost insert. Read the counts again.
                foreach (var tracked in rows.Concat(raised))
                    db.Entry(tracked).State = EntityState.Detached;
            }
        }
        return false;
    }

    private SignUpMailLog Raise(SignUpMailLog? row, string hash, string mailbox, string kind, DateTime today, DateTime now)
    {
        if (row is null)
            db.SignUpMailLogs.Add(row = new SignUpMailLog { AddressHash = hash, MailboxHash = mailbox, Kind = kind, Day = today, Count = 1, LastSentAt = now });
        else
        {
            row.Count++;
            row.LastSentAt = now;
        }
        return row;
    }

    // Removes the rows that no cooldown or cap can read any more, those from before yesterday.
    public async Task RemoveOldAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.Date.AddDays(-1);
        db.SignUpMailLogs.RemoveRange(await db.SignUpMailLogs.Where(l => l.Day < cutoff).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }
}
