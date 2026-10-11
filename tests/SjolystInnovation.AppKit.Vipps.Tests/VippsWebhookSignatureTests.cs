using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsWebhookSignatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string Secret = "webhook-secret";
    private const string Body = """{"reference":"order-42","name":"AUTHORIZED"}""";

    /// <summary>A request signed the way Vipps signs one.</summary>
    private static VippsWebhookRequest Signed(string body = Body, string secret = Secret, DateTimeOffset? signedAt = null,
        string method = "POST", string path = "/api/shop/webhook?x=1", string host = "garge-api.prod.tumogroup.com")
    {
        var date = (signedAt ?? Now).ToString("R");
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var canonical = $"{method}\n{path}\n{date};{host};{hash}";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
        return new VippsWebhookRequest
        {
            Method = method, PathAndQuery = path, Host = host, Body = body, Date = date, ContentSha256 = hash,
            Authorization = $"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={signature}",
        };
    }

    private static VippsWebhookResult Check(VippsWebhookRequest request, string secret = Secret) => VippsWebhookSignature.Verify(request, secret, Now).Result;

    [Fact]
    public void ASignedRequest_IsValid()
    {
        var result = VippsWebhookSignature.Verify(Signed(), Secret, Now);
        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.Zero, result.Age);
    }

    [Fact]
    public void AnotherSecret_IsABadSignature() => Assert.Equal(VippsWebhookResult.BadSignature, Check(Signed(), "other"));

    [Fact]
    public void ATamperedBody_IsABadContentHash() => Assert.Equal(VippsWebhookResult.BadContentHash, Check(Signed() with { Body = Body + " " }));

    [Fact]
    public void ABodyAndHashSwappedTogether_IsABadSignature()
    {
        const string other = """{"reference":"order-43"}""";
        var tampered = Signed() with { Body = other, ContentSha256 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(other))) };
        Assert.Equal(VippsWebhookResult.BadSignature, Check(tampered));
    }

    [Theory]
    [InlineData("GET", null, null)]
    [InlineData(null, "/api/other", null)]
    [InlineData(null, null, "evil.example")]
    public void AnotherMethodPathOrHost_IsABadSignature(string? method, string? path, string? host)
    {
        var request = Signed();
        request = request with { Method = method ?? request.Method, PathAndQuery = path ?? request.PathAndQuery, Host = host ?? request.Host };
        Assert.Equal(VippsWebhookResult.BadSignature, Check(request));
    }

    [Fact]
    public void NoSecret_IsMissingSecret() => Assert.Equal(VippsWebhookResult.MissingSecret, Check(Signed(), ""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("HMAC-SHA256 SignedHeaders=x")]
    [InlineData("hmac-sha256 Signature=abc")]
    public void AMissingOrForeignAuthorization_IsMissingHeader(string? authorization) =>
        Assert.Equal(VippsWebhookResult.MissingHeader, Check(Signed() with { Authorization = authorization }));

    [Fact]
    public void AMissingDateOrHash_IsMissingHeader()
    {
        Assert.Equal(VippsWebhookResult.MissingHeader, Check(Signed() with { Date = null }));
        Assert.Equal(VippsWebhookResult.MissingHeader, Check(Signed() with { ContentSha256 = "" }));
    }

    [Fact]
    public void AnUnreadableDate_IsBadDate() => Assert.Equal(VippsWebhookResult.BadDate, Check(Signed() with { Date = "yesterday" }));

    [Theory]
    [InlineData(-8 * 24 * 60 - 1)]
    [InlineData(6)]
    public void ADateOutsideTheRetryPeriod_IsStale(int minutesFromNow) =>
        Assert.Equal(VippsWebhookResult.Stale, Check(Signed(signedAt: Now.AddMinutes(minutesFromNow))));

    [Theory]
    [InlineData(-8 * 24 * 60)]
    [InlineData(-6 * 60)]
    [InlineData(5)]
    public void ARetrySignedEarlierInTheRetryPeriod_IsValid_AndReportsItsAge(int minutesFromNow)
    {
        var result = VippsWebhookSignature.Verify(Signed(signedAt: Now.AddMinutes(minutesFromNow)), Secret, Now);
        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.FromMinutes(-minutesFromNow), result.Age);
    }

    [Fact]
    public void TheRetryWindow_CoversVippsSevenDaysOfRetries() => Assert.True(VippsWebhookSignature.MaxAge >= TimeSpan.FromDays(7));
}
