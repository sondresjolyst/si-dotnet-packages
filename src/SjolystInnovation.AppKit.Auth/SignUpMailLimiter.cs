using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    public const string Network = "network";
    public const string OwnerBudget = "owner";

    private static readonly string[] Anonymous = [Verification, Notice];
    private static readonly string[] Owner = [Resend, Reminder];

    public static bool IsOwner(string kind) => Array.IndexOf(Owner, kind) >= 0;

    // The kinds that count against the same cap as the kind.
    public static string[] BudgetOf(string kind) => Array.IndexOf(Owner, kind) >= 0 ? Owner : Anonymous;
}

// The result of a reservation that also saves something for the caller.
internal enum MailReservation
{
    // Every count passed and the save of the caller was committed with the counts.
    Reserved,

    // A cap or the cooldown holds. Nothing was counted and the save was not called.
    Refused,

    // The save of the caller reported that it lost, so nothing was counted or saved.
    Lost,
}

/// <summary>
/// The hashes that the sign-up counts are kept under. They are HMAC-SHA256 with a key derived from
/// <see cref="AppKitAuthOptions.Key"/> and a fixed purpose label, so a copy of the table cannot be reversed by hashing guessed
/// addresses or networks. Changing the signing key changes every hash, so the counts start again.
/// </summary>
internal static class SignUpMailAddress
{
    private const string KeyPurpose = "SjolystInnovation.AppKit.Auth.SignUpMailLog";

    // The key of the hashes, from the signing key and the purpose label.
    public static byte[] DeriveKey(string signingKey) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(KeyPurpose));

    // The hash of the normalised address exactly as it is, as Identity normalises it for lookups.
    public static string Hash(byte[] key, string normalizedEmail) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalizedEmail.Trim())));

    // The hash of a network, from the shortened address of the caller. The prefix keeps it apart from the hash of an email.
    public static string NetworkHash(byte[] key, string shortenedAddress) => Hash(key, "network:" + shortenedAddress);

    // The hash of the mailbox key of the normalised address.
    public static string MailboxHash(byte[] key, string normalizedEmail) => Hash(key, MailboxKey(normalizedEmail));

    /// <summary>
    /// The key that stands for the mailbox an address reaches, so spellings of one inbox share one key. It is a best effort and
    /// covers only these rules. The address is normalised the way Identity does it, upper case. A <c>+tag</c> in the local part is
    /// dropped. A trailing dot on the domain is dropped. An internationalised domain becomes its ASCII form with
    /// <see cref="IdnMapping"/>. <c>googlemail.com</c> counts as <c>gmail.com</c>, and for that domain the dots of the local
    /// part are removed. No other provider rule is applied, so other spellings of one inbox still count apart.
    /// </summary>
    /// <param name="normalizedEmail">The address as Identity normalises it for lookups.</param>
    public static string MailboxKey(string normalizedEmail)
    {
        var key = normalizedEmail.Trim();
        var at = key.LastIndexOf('@');
        if (at <= 0)
            return key;
        var local = key[..at];
        var domain = key[(at + 1)..].TrimEnd('.');
        var plus = local.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
            local = local[..plus];
        try
        {
            domain = Idn.GetAscii(domain);
        }
        catch (ArgumentException)
        {
            // A domain that cannot be converted stays as it is.
        }
        domain = domain.ToUpperInvariant();
        if (domain == "GOOGLEMAIL.COM")
            domain = "GMAIL.COM";
        if (domain == "GMAIL.COM")
            local = local.Replace(".", string.Empty, StringComparison.Ordinal);
        return string.Concat(local, "@", domain);
    }

    private static readonly IdnMapping Idn = new();
}

// Decides whether a sign-up email may go to an address now, and counts it when it may. The counts live in the database,
// so the caps and the cooldown hold across instances and restarts. The rows are keyed by keyed hashes (see SignUpMailAddress),
// so changing the signing key resets the counts. The caller must not hold a transaction around the call and must have no
// pending changes on the context, and sends the email only after the counts are saved. TryReserveAsync returns true for that.
// ReserveWithAsync also runs a save of the caller inside the transaction of the counts, after every count has passed, and
// commits both or neither. A code that is issued for the email is saved there, so it is never stored for an email that did
// not go out. The caller issues the code inside that save, because the step that makes the rows saves every pending change
// on the context, and it does so outside the transaction.
//
// Three rules hold, each with its own running count row per day. The anonymous count per mailbox, the owner count per
// account, and the total per mailbox over every kind. A row per kind also keeps the time of the last email for the cooldown.
// Every count is raised by one conditional UPDATE that the database runs as one statement. The cap, and the cooldown where
// there is one, are in its WHERE, and zero changed rows means the cap or the cooldown holds. So parallel requests never pass a
// cap and none needs a retry. A reservation raises its rows in a fixed order, total, budget, kind, inside one transaction on the
// context, and rolls back at the first refusal, so a refusal by a later count leaves the earlier ones unchanged. A missing row
// is created first, with a count of zero, outside that transaction. Two creators can collide on the unique index of hash, kind
// and day. The loser detaches its entity, checks that the row exists, and goes on, so a lost insert never aborts the
// transaction of the counts and never leaves a failed entity tracked. It needs no raw SQL and no provider specific code.
internal sealed class SignUpMailLimiter<TContext>(TContext db, TimeProvider time, IOptions<AppKitAuthOptions> auth, ILookupNormalizer? normalizer = null)
    where TContext : DbContext, IAccountConsentDbContext
{
    // One count row to raise. A cap of int.MaxValue means the row only keeps the time of the last email.
    private readonly record struct Counter(string Hash, string Kind, int Cap, TimeSpan Cooldown);

    private readonly byte[] _key = SignUpMailAddress.DeriveKey(auth.Value.Key);

    // Counts one email of the kind for the address and returns true only when every rule that applies allows it. Nothing is
    // counted when it returns false. The anonymous kinds count per mailbox against the anonymous cap. The owner kinds count
    // per account, by its exact normalised address, against the owner cap. Over every kind and every account, a mailbox gets
    // at most the two caps together per day. A cooldown holds between emails of the same kind. The key is the normalised
    // address the account stores, or the request's address normalised the way Identity does it when there is no account,
    // so every spelling that finds the account shares its counters.
    public async Task<bool> TryReserveAsync(string email, string? storedNormalizedEmail, string kind, int anonymousCap, int ownerCap, TimeSpan cooldown, CancellationToken cancellationToken) =>
        await ReserveWithAsync(email, storedNormalizedEmail, kind, anonymousCap, ownerCap, cooldown, () => Task.FromResult(true), cancellationToken) == MailReservation.Reserved;

    // Counts like TryReserveAsync and also runs the save of the caller in the same transaction, after every count has passed.
    // The save returns false when it lost a race. Then nothing is counted and nothing is saved. The save is not called when a
    // count refuses.
    public Task<MailReservation> ReserveWithAsync(string email, string? storedNormalizedEmail, string kind, int anonymousCap, int ownerCap, TimeSpan cooldown, Func<Task<bool>> save, CancellationToken cancellationToken)
    {
        var exact = storedNormalizedEmail ?? (normalizer?.NormalizeEmail(email.Trim()) ?? email.Trim().ToUpperInvariant());
        var mailbox = SignUpMailAddress.MailboxHash(_key, exact);
        var isOwner = SignUpMailKinds.IsOwner(kind);
        var hash = isOwner ? SignUpMailAddress.Hash(_key, exact) : mailbox;
        return RaiseAsync(mailbox,
        [
            new Counter(mailbox, SignUpMailKinds.Total, anonymousCap + ownerCap, TimeSpan.Zero),
            new Counter(hash, isOwner ? SignUpMailKinds.OwnerBudget : SignUpMailKinds.AnonymousBudget, isOwner ? ownerCap : anonymousCap, TimeSpan.Zero),
            new Counter(hash, kind, int.MaxValue, cooldown),
        ], save, cancellationToken);
    }

    // Counts one sign-up from the network, given as the shortened address of the caller, and returns true only when
    // the network has not reached the cap today. The row keeps the network hash in both hash columns.
    public Task<bool> TryCountNetworkAsync(string shortenedAddress, int cap, CancellationToken cancellationToken)
    {
        var networkHash = SignUpMailAddress.NetworkHash(_key, shortenedAddress);
        return TryRaiseAsync(networkHash, [new Counter(networkHash, SignUpMailKinds.Network, cap, TimeSpan.Zero)], cancellationToken);
    }

    private async Task<bool> TryRaiseAsync(string mailbox, Counter[] counters, CancellationToken cancellationToken) =>
        await RaiseAsync(mailbox, counters, () => Task.FromResult(true), cancellationToken) == MailReservation.Reserved;

    // Raises every counter by one, in the order given, or none of them. The save runs after the last counter and before the commit.
    private async Task<MailReservation> RaiseAsync(string mailbox, Counter[] counters, Func<Task<bool>> save, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var today = now.Date;
        await EnsureRowsAsync(mailbox, counters, today, cancellationToken);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (hash, kind, cap, cooldown) in counters)
        {
            var row = db.SignUpMailLogs.Where(l => l.AddressHash == hash && l.Kind == kind && l.Day == today && l.Count < cap);
            if (cooldown > TimeSpan.Zero)
            {
                var sentBefore = now - cooldown;
                row = row.Where(l => l.LastSentAt <= sentBefore);
            }
            var changed = await row.ExecuteUpdateAsync(set => set.SetProperty(l => l.Count, l => l.Count + 1).SetProperty(l => l.LastSentAt, now), cancellationToken);
            if (changed == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return MailReservation.Refused;
            }
        }
        if (!await save())
        {
            await tx.RollbackAsync(cancellationToken);
            return MailReservation.Lost;
        }
        await tx.CommitAsync(cancellationToken);
        return MailReservation.Reserved;
    }

    // Makes the rows of today that the counters need, with a count of zero. A new row starts with the time of the last email of
    // the row of yesterday, so a cooldown holds across midnight.
    private async Task EnsureRowsAsync(string mailbox, Counter[] counters, DateTime today, CancellationToken cancellationToken)
    {
        var yesterday = today.AddDays(-1);
        var rows = await db.SignUpMailLogs.AsNoTracking().Where(l => l.MailboxHash == mailbox && l.Day >= yesterday)
            .Select(l => new { l.AddressHash, l.Kind, l.Day, l.LastSentAt }).ToListAsync(cancellationToken);
        foreach (var (hash, kind, _, _) in counters)
        {
            if (rows.Any(r => r.AddressHash == hash && r.Kind == kind && r.Day == today))
                continue;
            var lastSentAt = rows.FirstOrDefault(r => r.AddressHash == hash && r.Kind == kind && r.Day == yesterday)?.LastSentAt ?? DateTime.UnixEpoch;
            var created = new SignUpMailLog { AddressHash = hash, MailboxHash = mailbox, Kind = kind, Day = today, Count = 0, LastSentAt = lastSentAt };
            db.SignUpMailLogs.Add(created);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another request made the row first. Anything else is a real failure.
                db.Entry(created).State = EntityState.Detached;
                if (!await db.SignUpMailLogs.AnyAsync(l => l.AddressHash == hash && l.Kind == kind && l.Day == today, cancellationToken))
                    throw;
            }
        }
    }

    // Removes the rows that no cooldown or cap can read any more, those from before yesterday. Two hosts may run it at once.
    public Task RemoveOldAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.Date.AddDays(-1);
        return db.SignUpMailLogs.Where(l => l.Day < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
