using System.Net;
using System.Text.Json;
using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsFailuresTests
{
    private static HttpRequestException Status(int status) =>
        new("failed", null, (HttpStatusCode)status);

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(499)]
    public void A4xxOtherThan408_409And429_IsARefusal(int status)
    {
        Assert.True(VippsFailures.IsRefusal(Status(status)));
        Assert.Equal(VippsFailureKind.Refused, VippsFailures.Classify(Status(status), CancellationToken.None));
        Assert.True(VippsFailures.IsProviderFailure(Status(status), CancellationToken.None));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void A408_409_429Or5xx_MayHaveGoneThrough(int status)
    {
        Assert.False(VippsFailures.IsRefusal(Status(status)));
        Assert.Equal(VippsFailureKind.Uncertain, VippsFailures.Classify(Status(status), CancellationToken.None));
        Assert.True(VippsFailures.IsProviderFailure(Status(status), CancellationToken.None));
    }

    [Fact]
    public void ATransportErrorWithoutAStatus_MayHaveGoneThrough()
    {
        var ex = new HttpRequestException("connection reset");
        Assert.False(VippsFailures.IsRefusal(ex));
        Assert.Equal(VippsFailureKind.Uncertain, VippsFailures.Classify(ex, CancellationToken.None));
    }

    [Fact]
    public void ACancellationWhileTheCallersTokenIsNotCancelled_IsATimeoutThatMayHaveGoneThrough()
    {
        var ex = new TaskCanceledException("timed out", new TimeoutException());
        Assert.Equal(VippsFailureKind.Uncertain, VippsFailures.Classify(ex, CancellationToken.None));
        Assert.True(VippsFailures.IsProviderFailure(ex, CancellationToken.None));
        Assert.False(VippsFailures.IsCallerCancellation(ex, CancellationToken.None));
    }

    [Fact]
    public void ACancellationOfTheCallersToken_IsTheCallerCancelling()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new TaskCanceledException("canceled", null, cts.Token);
        Assert.Equal(VippsFailureKind.CallerCancelled, VippsFailures.Classify(ex, cts.Token));
        Assert.False(VippsFailures.IsProviderFailure(ex, cts.Token));
        Assert.True(VippsFailures.IsCallerCancellation(ex, cts.Token));
    }

    [Fact]
    public void AnHttpErrorAfterTheCallerCancelled_StillCountsAsTheVippsAnswer()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(VippsFailureKind.Refused, VippsFailures.Classify(Status(400), cts.Token));
        Assert.Equal(VippsFailureKind.Uncertain, VippsFailures.Classify(Status(503), cts.Token));
    }

    [Fact]
    public void AnyOtherException_IsNotAVippsFailure()
    {
        Assert.Equal(VippsFailureKind.None, VippsFailures.Classify(new JsonException(), CancellationToken.None));
        Assert.Equal(VippsFailureKind.None, VippsFailures.Classify(new InvalidOperationException(), CancellationToken.None));
        Assert.False(VippsFailures.IsProviderFailure(new InvalidOperationException(), CancellationToken.None));
        Assert.False(VippsFailures.IsRefusal(new InvalidOperationException()));
    }

    [Fact]
    public void RecommendedTimeout_Is15Seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), VippsFailures.RecommendedTimeout);

    [Fact]
    public async Task TheClientsTimeout_ClassifiesAsUncertain()
    {
        using var http = new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(50) };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(new Uri("https://api.vipps.no/epayment/v1/payments/x")));
        Assert.Equal(VippsFailureKind.Uncertain, VippsFailures.Classify(ex, CancellationToken.None));
    }

    [Fact]
    public async Task TheCallersCancellation_ClassifiesAsCallerCancelled()
    {
        using var http = new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(new Uri("https://api.vipps.no/epayment/v1/payments/x"), cts.Token));
        Assert.Equal(VippsFailureKind.CallerCancelled, VippsFailures.Classify(ex, cts.Token));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
