using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsPaymentEventChecksTests
{
    private static VippsWebhookDelivery<VippsPaymentEvent> Delivery(string name, int? amount, bool success = true, bool isTest = false) => new()
    {
        Event = new VippsPaymentEvent
        {
            Reference = "order-1",
            Name = name,
            Amount = amount is { } value ? new VippsWebhookAmount { Value = value, Currency = "NOK" } : null,
            Success = success,
        },
        IsTest = isTest,
        EventKey = "psp-1",
        Slot = "shop",
    };

    [Theory]
    [InlineData("AUTHORIZED", 1000, true)]
    [InlineData("AUTHORIZED", 999, false)]
    [InlineData("AUTHORIZED", 1001, false)]
    [InlineData("AUTHORIZED", null, false)]
    [InlineData("CAPTURED", 1, true)]
    [InlineData("CAPTURED", 1000, true)]
    [InlineData("CAPTURED", 1001, false)]
    [InlineData("CAPTURED", 0, false)]
    [InlineData("CAPTURED", -5, false)]
    [InlineData("CAPTURED", null, false)]
    [InlineData("REFUNDED", 400, true)]
    [InlineData("REFUNDED", 1001, false)]
    [InlineData("REFUNDED", 0, false)]
    [InlineData("REFUNDED", null, false)]
    [InlineData("CANCELLED", null, true)]
    [InlineData("CANCELLED", 99999, true)]
    [InlineData("ABORTED", null, true)]
    [InlineData("EXPIRED", null, true)]
    [InlineData("TERMINATED", null, true)]
    [InlineData("CREATED", 5, true)]
    public void AmountFits_AuthorizedMatchesTheTotalAndCapturedAndRefundedStayWithinIt(string name, int? amount, bool fits)
    {
        Assert.Equal(fits, VippsPaymentEventChecks.AmountFits(name, amount, 1000));
        Assert.Equal(fits, VippsPaymentEventChecks.AmountFits(Delivery(name, amount).Event, 1000));
    }

    [Fact]
    public void EnvironmentMatches_ComparesTheSigningEnvironmentWithTheSalesUnits()
    {
        Assert.True(VippsPaymentEventChecks.EnvironmentMatches(Delivery("AUTHORIZED", 1000, isTest: true), isTest: true));
        Assert.True(VippsPaymentEventChecks.EnvironmentMatches(Delivery("AUTHORIZED", 1000, isTest: false), isTest: false));
        Assert.False(VippsPaymentEventChecks.EnvironmentMatches(Delivery("AUTHORIZED", 1000, isTest: true), isTest: false));
        Assert.False(VippsPaymentEventChecks.EnvironmentMatches(Delivery("AUTHORIZED", 1000, isTest: false), isTest: true));
    }

    [Fact]
    public void Check_AGoodEvent_HasNoProblem() =>
        Assert.Equal(VippsPaymentEventProblem.None, VippsPaymentEventChecks.Check(Delivery("CAPTURED", 1000), isTest: false, totalInOre: 1000));

    [Fact]
    public void Check_TheWrongEnvironment_ComesFirst() =>
        Assert.Equal(VippsPaymentEventProblem.WrongEnvironment,
            VippsPaymentEventChecks.Check(Delivery("AUTHORIZED", 5, success: false, isTest: true), isTest: false, totalInOre: 1000));

    [Fact]
    public void Check_AFailedEvent_ComesBeforeItsAmount() =>
        Assert.Equal(VippsPaymentEventProblem.NotSuccessful,
            VippsPaymentEventChecks.Check(Delivery("AUTHORIZED", 5, success: false), isTest: false, totalInOre: 1000));

    [Fact]
    public void Check_AnAmountThatDoesNotFit_IsAMismatch() =>
        Assert.Equal(VippsPaymentEventProblem.AmountMismatch,
            VippsPaymentEventChecks.Check(Delivery("REFUNDED", 1500), isTest: false, totalInOre: 1000));

    [Fact]
    public void Checks_RejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => VippsPaymentEventChecks.AmountFits((VippsPaymentEvent)null!, 1000));
        Assert.Throws<ArgumentNullException>(() => VippsPaymentEventChecks.EnvironmentMatches<VippsPaymentEvent>(null!, false));
        Assert.Throws<ArgumentNullException>(() => VippsPaymentEventChecks.Check(null!, false, 1000));
    }
}
