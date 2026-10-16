using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>The result of a webhook signature check.</summary>
public enum VippsWebhookResult
{
    /// <summary>The request passed every check.</summary>
    Valid,
    /// <summary>No secret was given to check against.</summary>
    MissingSecret,
    /// <summary>The <c>Authorization</c>, <c>x-ms-date</c>, or <c>x-ms-content-sha256</c> header is missing or malformed.</summary>
    MissingHeader,
    /// <summary>The signed date cannot be parsed.</summary>
    BadDate,
    /// <summary>The signed date is older than <see cref="VippsWebhookSignature.MaxAge"/> or too far in the future.</summary>
    Stale,
    /// <summary>The body does not match its content hash.</summary>
    BadContentHash,
    /// <summary>The signature does not match.</summary>
    BadSignature,
}

/// <summary>The parts of a webhook request the signature covers.</summary>
public sealed record VippsWebhookRequest
{
    /// <summary>The HTTP method, for example <c>POST</c>.</summary>
    public required string Method { get; init; }
    /// <summary>The path and query string, for example <c>/api/webhooks/vipps?x=1</c>.</summary>
    public required string PathAndQuery { get; init; }
    /// <summary>The <c>Host</c> header as received.</summary>
    public required string Host { get; init; }
    /// <summary>The <c>Authorization</c> header.</summary>
    public string? Authorization { get; init; }
    /// <summary>The <c>x-ms-date</c> header.</summary>
    public string? Date { get; init; }
    /// <summary>The <c>x-ms-content-sha256</c> header.</summary>
    public string? ContentSha256 { get; init; }
    /// <summary>
    /// The body as text. The check hashes its UTF-8 bytes unless <see cref="RawBody"/> is set. Defaults to
    /// an empty string.
    /// </summary>
    public string Body { get; init; } = string.Empty;
    /// <summary>
    /// The body bytes exactly as received. When set, the check hashes these instead of <see cref="Body"/>,
    /// so a byte order mark or an encoding quirk cannot change the hash.
    /// </summary>
    public ReadOnlyMemory<byte>? RawBody { get; init; }
}

/// <summary>The outcome of a check, and how long ago the request was signed.</summary>
public readonly record struct VippsWebhookVerification(VippsWebhookResult Result, TimeSpan Age)
{
    /// <summary>Whether the request passed every check.</summary>
    public bool IsValid => Result == VippsWebhookResult.Valid;
}

/// <summary>Checks the HMAC-SHA256 signature Vipps puts on each webhook request.</summary>
public static partial class VippsWebhookSignature
{
    /// <summary>
    /// The maximum age of an accepted signed date. Vipps retries a failed webhook for up to seven days
    /// and does not document whether a retry is signed again, so a signed date may be that old. The
    /// receiver must stop replays itself, by keeping the event ids it processed for longer than this.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(8);

    /// <summary>How far a signed date may lie in the future, for clock differences.</summary>
    public static readonly TimeSpan MaxClockAhead = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"Signature=([^,&\s]+)")]
    private static partial Regex SignaturePattern();

    /// <summary>Checks the content hash, the signed date, and the signature of a request.</summary>
    public static VippsWebhookVerification Verify(VippsWebhookRequest request, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret)) return new(VippsWebhookResult.MissingSecret, TimeSpan.Zero);

        var headers = CheckHeaders(request, now);
        if (!headers.IsValid) return headers;
        var age = headers.Age;
        var match = SignaturePattern().Match(request.Authorization!);

        var bodyHash = Convert.ToBase64String(request.RawBody is { } raw
            ? SHA256.HashData(raw.Span)
            : SHA256.HashData(Encoding.UTF8.GetBytes(request.Body)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(bodyHash), Encoding.ASCII.GetBytes(request.ContentSha256!)))
            return new(VippsWebhookResult.BadContentHash, age);

        // METHOD\n<path and query>\n<x-ms-date>;<host>;<x-ms-content-sha256>
        var canonical = $"{request.Method}\n{request.PathAndQuery}\n{request.Date};{request.Host};{request.ContentSha256}";
        var computed = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
        var valid = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(match.Groups[1].Value));
        return new(valid ? VippsWebhookResult.Valid : VippsWebhookResult.BadSignature, age);
    }

    /// <summary>
    /// Checks the headers and the signed date without a secret or the body, so a receiver can turn away an
    /// unsigned or stale request before it reads the body. Valid means the headers allow a full check.
    /// </summary>
    internal static VippsWebhookVerification CheckHeaders(VippsWebhookRequest request, DateTimeOffset now)
    {
        var auth = request.Authorization ?? string.Empty;
        if (!auth.StartsWith("HMAC-SHA256 ", StringComparison.Ordinal) || !SignaturePattern().IsMatch(auth))
            return new(VippsWebhookResult.MissingHeader, TimeSpan.Zero);
        if (string.IsNullOrEmpty(request.Date) || string.IsNullOrEmpty(request.ContentSha256)) return new(VippsWebhookResult.MissingHeader, TimeSpan.Zero);

        if (!DateTimeOffset.TryParse(request.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var signedAt))
            return new(VippsWebhookResult.BadDate, TimeSpan.Zero);
        var age = now - signedAt;
        if (age > MaxAge || -age > MaxClockAhead) return new(VippsWebhookResult.Stale, age);
        return new(VippsWebhookResult.Valid, age);
    }
}
