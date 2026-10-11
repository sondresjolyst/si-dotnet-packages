using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Si.Vipps;

public enum VippsWebhookResult
{
    Valid,
    MissingSecret,
    MissingHeader,
    BadDate,
    Stale,
    BadContentHash,
    BadSignature,
}

/// <summary>The parts of a webhook request the signature covers.</summary>
public sealed record VippsWebhookRequest
{
    /// <summary>The HTTP method, such as <c>POST</c>.</summary>
    public required string Method { get; init; }
    /// <summary>The path and query string, such as <c>/api/webhooks/vipps?x=1</c>.</summary>
    public required string PathAndQuery { get; init; }
    /// <summary>The <c>Host</c> header as received.</summary>
    public required string Host { get; init; }
    public string? Authorization { get; init; }
    /// <summary>The <c>x-ms-date</c> header.</summary>
    public string? Date { get; init; }
    /// <summary>The <c>x-ms-content-sha256</c> header.</summary>
    public string? ContentSha256 { get; init; }
    /// <summary>The raw body, exactly as received.</summary>
    public required string Body { get; init; }
}

/// <summary>The outcome of a check, and how long ago the request was signed.</summary>
public readonly record struct VippsWebhookVerification(VippsWebhookResult Result, TimeSpan Age)
{
    public bool IsValid => Result == VippsWebhookResult.Valid;
}

/// <summary>Checks the HMAC-SHA256 signature Vipps puts on each webhook request.</summary>
public static partial class VippsWebhookSignature
{
    /// <summary>
    /// The oldest signed date accepted. Vipps retries a failed webhook for up to seven days and does
    /// not document whether a retry is signed again, so a signed date may be that old. A replay is
    /// for the receiver to stop, by keeping the event ids it processed for longer than this.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(8);

    /// <summary>How far a signed date may lie in the future, for clock differences.</summary>
    public static readonly TimeSpan MaxClockAhead = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"Signature=([^,&\s]+)")]
    private static partial Regex SignaturePattern();

    /// <summary>Checks the content hash, the signed date and the signature of a request.</summary>
    public static VippsWebhookVerification Verify(VippsWebhookRequest request, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret)) return new(VippsWebhookResult.MissingSecret, TimeSpan.Zero);

        var auth = request.Authorization ?? string.Empty;
        if (!auth.StartsWith("HMAC-SHA256 ", StringComparison.Ordinal)) return new(VippsWebhookResult.MissingHeader, TimeSpan.Zero);
        var match = SignaturePattern().Match(auth);
        if (!match.Success) return new(VippsWebhookResult.MissingHeader, TimeSpan.Zero);
        if (string.IsNullOrEmpty(request.Date) || string.IsNullOrEmpty(request.ContentSha256)) return new(VippsWebhookResult.MissingHeader, TimeSpan.Zero);

        if (!DateTimeOffset.TryParse(request.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var signedAt))
            return new(VippsWebhookResult.BadDate, TimeSpan.Zero);
        var age = now - signedAt;
        if (age > MaxAge || -age > MaxClockAhead) return new(VippsWebhookResult.Stale, age);

        var bodyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(request.Body)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(bodyHash), Encoding.ASCII.GetBytes(request.ContentSha256)))
            return new(VippsWebhookResult.BadContentHash, age);

        // METHOD\n<path and query>\n<x-ms-date>;<host>;<x-ms-content-sha256>
        var canonical = $"{request.Method}\n{request.PathAndQuery}\n{request.Date};{request.Host};{request.ContentSha256}";
        var computed = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
        var valid = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(match.Groups[1].Value));
        return new(valid ? VippsWebhookResult.Valid : VippsWebhookResult.BadSignature, age);
    }
}
