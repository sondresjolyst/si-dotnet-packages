using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsPaymentPollingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static VippsPayment Payment(string state, int authorized = 0, int captured = 0, int refunded = 0, int cancelled = 0) => new()
    {
        Reference = "order-1",
        State = state,
        AuthorizedAmountInOre = authorized,
        CapturedAmountInOre = captured,
        RefundedAmountInOre = refunded,
        CancelledAmountInOre = cancelled,
        CaptureGuaranteedUntil = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero),
    };

    private static IEnumerable<(string Name, int? Amount)> Names(IEnumerable<VippsPaymentEvent> events) =>
        events.Select(e => (e.Name, e.Amount?.Value));

    [Theory]
    [InlineData("ABORTED")]
    [InlineData("EXPIRED")]
    [InlineData("TERMINATED")]
    public void AnEndedPayment_ImpliesItsOwnState(string state) =>
        Assert.Equal([(state, (int?)null)], Names(VippsPaymentPolling.EventsOf(Payment(state, authorized: 1000), Now)));

    [Fact]
    public void AnAuthorizedPaymentWithNothingElse_ImpliesAuthorizedWithTheAuthorizedAmount()
    {
        var e = Assert.Single(VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000), Now));
        Assert.Equal(VippsPaymentEventNames.Authorized, e.Name);
        Assert.Equal(1000, e.Amount!.Value);
        Assert.Equal("NOK", e.Amount.Currency);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), e.CaptureGuaranteedUntil);
    }

    [Fact]
    public void OnlyAuthorized_CarriesTheProfile()
    {
        var authorized = Assert.Single(VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000) with { ProfileSub = "sub-1" }, Now));
        Assert.Equal("sub-1", authorized.Sub);
        var captured = Assert.Single(VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000, captured: 1000) with { ProfileSub = "sub-1" }, Now));
        Assert.Null(captured.Sub);
        Assert.Null(captured.CaptureGuaranteedUntil);
    }

    [Fact]
    public void ACapture_ImpliesCapturedWithTheCapturedTotal() =>
        Assert.Equal([("CAPTURED", (int?)600)], Names(VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000, captured: 600), Now)));

    [Fact]
    public void ARefund_FollowsItsCapture() =>
        Assert.Equal([("CAPTURED", (int?)1000), ("REFUNDED", 400)],
            Names(VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000, captured: 1000, refunded: 400), Now)));

    [Fact]
    public void ACancellationWithoutACapture_ImpliesCancelled() =>
        Assert.Equal([("CANCELLED", (int?)1000)], Names(VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000, cancelled: 1000), Now)));

    [Fact]
    public void ACancellationOfTheRestAfterACapture_ImpliesOnlyTheCapture() =>
        Assert.Equal([("CAPTURED", (int?)600)], Names(VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000, captured: 600, cancelled: 400), Now)));

    [Theory]
    [InlineData("CREATED")]
    [InlineData("")]
    [InlineData("SOMETHING_NEW")]
    public void AnyOtherState_ImpliesNothing(string state) =>
        Assert.Empty(VippsPaymentPolling.EventsOf(Payment(state, authorized: 1000), Now));

    [Fact]
    public void Events_AreSuccessfulForThePaymentAtTheTimeOfThePoll()
    {
        var events = VippsPaymentPolling.EventsOf(Payment("AUTHORIZED", authorized: 1000, captured: 1000, refunded: 1000), Now);
        Assert.All(events, e =>
        {
            Assert.True(e.Success);
            Assert.Equal("order-1", e.Reference);
            Assert.Equal(Now, e.Timestamp);
            Assert.Null(e.Msn);
            Assert.Equal(string.Empty, e.PspReference);
        });
    }

    [Fact]
    public void Deliveries_CarryAPollKeyPerEventAndAmount()
    {
        var deliveries = VippsPaymentPolling.DeliveriesOf(Payment("AUTHORIZED", authorized: 1000, captured: 1000, refunded: 400), isTest: false, "shop", Now);
        Assert.Equal(["poll:order-1:CAPTURED:1000", "poll:order-1:REFUNDED:400"], deliveries.Select(d => d.EventKey));
        Assert.All(deliveries, d =>
        {
            Assert.False(d.IsTest);
            Assert.Equal("shop", d.Slot);
        });
        Assert.Equal("REFUNDED", deliveries[1].Event.Name);
    }

    [Fact]
    public void Deliveries_InTheTestEnvironment_HaveATestKey()
    {
        var d = Assert.Single(VippsPaymentPolling.DeliveriesOf(Payment("EXPIRED"), isTest: true, "shop", Now));
        Assert.True(d.IsTest);
        Assert.Equal("test:poll:order-1:EXPIRED:", d.EventKey);
    }

    [Fact]
    public void ANullPayment_Throws() =>
        Assert.Throws<ArgumentNullException>(() => VippsPaymentPolling.EventsOf(null!, Now));
}
