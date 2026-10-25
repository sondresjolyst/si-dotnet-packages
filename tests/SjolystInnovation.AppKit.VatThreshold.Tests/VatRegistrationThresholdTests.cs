using Xunit;
using static SjolystInnovation.AppKit.VatThreshold.Tests.Oslo;

namespace SjolystInnovation.AppKit.VatThreshold.Tests;

public class VatRegistrationThresholdTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static VatSale Sale(string reference, DateTime at, long amountInOre, params VatRefund[] refunds) =>
        new(reference, at, amountInOre, refunds);

    private static VatRefund Refund(DateTime at, long amountInOre) => new(at, amountInOre);

    private static DateTime Utc(int year, int month, int day, int hour = 12, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static Dictionary<DateOnly, long> NoOutside() => [];

    private static DateOnly Month(int year, int month) => new(year, month, 1);

    private static Dictionary<DateOnly, long> Outside(int year, int month, long amountInOre) => new() { [Month(year, month)] = amountInOre };

    [Fact]
    public void TheLimitIs50000Kroner_OverTwelveMonths()
    {
        Assert.Equal(5_000_000, VatRegistrationThreshold.LimitInOre);
        Assert.Equal(12, VatRegistrationThreshold.WindowMonths);
    }

    [Fact]
    public void Turnover_CountsTheLast12MonthsOfSalesLessRefunds_AndTheOutsideSales()
    {
        var status = StatusAt(
            [
                Sale("a", Now.AddMonths(-11), 1_000_000),
                Sale("b", Now.AddDays(-3), 500_000, Refund(Now.AddDays(-3).AddHours(1), 100_000)),
                Sale("old", Now.AddMonths(-13), 700_000),
            ],
            new Dictionary<DateOnly, long> { [Month(2026, 10)] = 20_000, [Month(2025, 11)] = 30_000, [Month(2025, 9)] = 999_000 },
            Now);

        Assert.Equal(1_000_000 + 400_000 + 20_000 + 30_000, status.TotalInOre);
        Assert.Equal(0, VatRegistrationThreshold.WarningLevel(status.TotalInOre, [80]));
        Assert.Null(status.Crossing);
    }

    [Fact]
    public void ARefundAboveTheSale_CountsAsNothing()
    {
        var status = StatusAt(
            [Sale("a", Now.AddDays(-3), 300_000), Sale("b", Now.AddDays(-3), 100_000, Refund(Now.AddDays(-3).AddHours(1), 200_000))],
            NoOutside(), Now);

        Assert.Equal(300_000, status.TotalInOre);
    }

    [Fact]
    public void Months_AreTheMonthsTheWindowEndingTodayOverlaps_OldestFirst()
    {
        // The window ending 19 October 2026 starts 20 October 2025, so it overlaps 13 months.
        var status = StatusAt(
            [Sale("a", Now.AddMonths(-1), 10_000), Sale("b", Now.AddMonths(-1).AddDays(2), 5_000)],
            Outside(2026, 9, 7_000),
            Now);

        Assert.Equal(13, status.Turnover.Months.Count);
        Assert.Equal(Month(2025, 10), status.Turnover.Months[0].Month);
        Assert.Equal(Month(2026, 10), status.Turnover.Months[^1].Month);
        Assert.Equal(new VatTurnoverMonth(Month(2026, 9), 15_000, 7_000), status.Turnover.Months[^2]);
        Assert.Equal(22_000, status.TotalInOre);
    }

    [Fact]
    public void Months_OnTheLastDayOfAMonth_AreTwelve()
    {
        var status = StatusAt([], NoOutside(), Utc(2026, 10, 31));

        Assert.Equal(12, status.Turnover.Months.Count);
        Assert.Equal(Month(2025, 11), status.Turnover.Months[0].Month);
    }

    [Fact]
    public void Months_CountOnlyTheSalesInsideTheWindow_AndAllOfTheOutsideSales()
    {
        var status = StatusAt([Sale("out", Utc(2025, 10, 19), 100), Sale("in", Utc(2025, 10, 20), 1)], Outside(2025, 10, 5_000), Now);

        Assert.Equal(new VatTurnoverMonth(Month(2025, 10), 1, 5_000), status.Turnover.Months[0]);
        Assert.Equal(5_001, status.TotalInOre);
    }

    [Fact]
    public void Months_FollowNorwegianTime()
    {
        // 22:30 UTC on 30 September is 00:30 on 1 October in Oslo.
        var status = StatusAt([Sale("a", Utc(2026, 9, 30, 22, 30), 10_000)], NoOutside(), Now);

        Assert.Equal(10_000, status.Month(2026, 10).SalesInOre);
        Assert.Equal(0, status.Month(2026, 9).SalesInOre);
    }

    [Fact]
    public void Window_StartsTheDayAfterTheSameDateAYearBack()
    {
        // Today is 19 October 2026, so the window starts 20 October 2025.
        var status = StatusAt([Sale("old", Utc(2025, 10, 19), 100), Sale("in", Utc(2025, 10, 20), 1)], NoOutside(), Now);

        Assert.Equal(1, status.TotalInOre);
    }

    [Fact]
    public void Window_FollowsNorwegianTime()
    {
        // 22:30 UTC on 19 October 2025 is 00:30 on 20 October in Oslo, so the sale is inside the window.
        var status = StatusAt([Sale("a", Utc(2025, 10, 19, 22, 30), 100)], NoOutside(), Now);

        Assert.Equal(100, status.TotalInOre);
        Assert.Equal(new DateOnly(2025, 10, 20), VatRegistrationThreshold.DayOf(Utc(2025, 10, 19, 22, 30), Zone));
    }

    [Theory]
    [InlineData(2026, 10, 19, 2025, 10, 20)]
    [InlineData(2026, 3, 31, 2025, 4, 1)]
    [InlineData(2026, 1, 1, 2025, 1, 2)]
    [InlineData(2025, 12, 31, 2025, 1, 1)]
    [InlineData(2028, 2, 29, 2027, 3, 1)]
    [InlineData(2028, 2, 28, 2027, 3, 1)]
    [InlineData(2025, 2, 28, 2024, 2, 29)]
    [InlineData(2025, 3, 1, 2024, 3, 2)]
    public void WindowStart_IsTheDayAfterTheSameDateTwelveMonthsBack(int y, int m, int d, int sy, int sm, int sd)
    {
        Assert.Equal(new DateOnly(sy, sm, sd), VatRegistrationThreshold.WindowStart(new DateOnly(y, m, d)));
    }

    [Theory]
    [InlineData(1, "second")]
    [InlineData(-1, null)]
    public void AWindowEndingOnALeapDay_StartsOnTheFirstOfMarch(int dayFromFirstOfMarch, string? crossing)
    {
        // The window ending 29 February 2028 starts 1 March 2027.
        var first = dayFromFirstOfMarch > 0 ? Utc(2027, 3, 1) : Utc(2027, 2, 28);
        var status = StatusAt(
            [Sale("first", first, 3_000_000), Sale("second", Utc(2028, 2, 29), 2_500_000)],
            NoOutside(), Utc(2028, 3, 10));

        Assert.Equal(crossing, status.Reference);
        Assert.Equal(crossing == null ? null : "2028-02-29", status.CrossedOn);
    }

    [Fact]
    public void AWindowStartingMidMonth_PassesTheThreshold_EvenWhenNoCalendarMonthWindowDoes()
    {
        // 15 March 2025 to 10 March 2026 is inside the window ending 10 March 2026, but no run of 12 calendar months
        // holds both sales.
        var status = StatusAt([Sale("first", Utc(2025, 3, 15), 3_000_000), Sale("second", Utc(2026, 3, 10), 2_500_000)], NoOutside(), Now);

        Assert.Equal("second", status.Reference);
        Assert.Equal("2026-03-10", status.CrossedOn);
        Assert.Equal("2026-03", status.CrossedMonth);
        Assert.Equal(Utc(2026, 3, 10), status.CrossedAt);
    }

    [Theory]
    [InlineData(16, "second")]
    [InlineData(15, null)]
    public void ASaleOnTheSameDateAYearEarlier_IsOutsideTheWindow_TheDayAfterIsInside(int firstDay, string? crossing)
    {
        var status = StatusAt([Sale("first", Utc(2025, 3, firstDay), 3_000_000), Sale("second", Utc(2026, 3, 15), 2_500_000)], NoOutside(), Now);

        Assert.Equal(crossing, status.Reference);
        Assert.Equal(crossing == null ? null : "2026-03-15", status.CrossedOn);
    }

    [Theory]
    [InlineData(30, "sale")]
    [InlineData(31, null)]
    public void OutsideSales_CountInEveryWindowTheirMonthOverlaps(int saleDay, string? crossing)
    {
        // The window ending 30 March 2026 starts 31 March 2025 and overlaps March 2025. The one ending 31 March starts 1 April.
        var status = StatusAt([Sale("sale", Utc(2026, 3, saleDay, 10), 200_000)], Outside(2025, 3, 4_900_000), Now);

        Assert.Equal(crossing, status.Reference);
    }

    [Fact]
    public void AnOutsideMonthPartlyInsideTheWindowEndingToday_CountsInTheTurnover()
    {
        // The window ending 19 October 2026 holds only 20 to 31 October 2025.
        var status = StatusAt([], Outside(2025, 10, 4_000_000), Now);

        Assert.Equal(4_000_000, status.TotalInOre);
        Assert.Equal(80, VatRegistrationThreshold.WarningLevel(status.TotalInOre, [80]));
    }

    [Fact]
    public void ARefund_OfASaleCapturedBeforeTheWindow_DoesNotLowerIt()
    {
        var status = StatusAt(
            [Sale("before", Utc(2025, 10, 10), 500_000, Refund(Utc(2026, 3, 1), 500_000)), Sale("in", Utc(2026, 2, 1), 2_000_000)],
            NoOutside(), Now);

        Assert.Equal(2_000_000, status.TotalInOre);
        Assert.Equal(2_000_000, status.Turnover.Months.Sum(m => m.SalesInOre));
    }

    [Fact]
    public void ARefund_InsideTheWindow_LowersIt()
    {
        var status = StatusAt([Sale("a", Utc(2026, 9, 10), 1_000_000, Refund(Utc(2026, 10, 1), 400_000))], NoOutside(), Now);

        Assert.Equal(600_000, status.TotalInOre);
        Assert.Equal(600_000, status.Month(2026, 9).SalesInOre);
    }

    [Fact]
    public void ARefund_AfterTheSaleThatPassedTheThreshold_DoesNotUndoTheCrossing()
    {
        // The window ending 10 March 2026 held 55 000 kr. The refund in May comes after that window has ended.
        var status = StatusAt(
            [Sale("a", Utc(2026, 1, 10), 3_000_000, Refund(Utc(2026, 5, 2), 1_000_000)), Sale("b", Utc(2026, 3, 10), 2_500_000)],
            NoOutside(), Now);

        Assert.Equal("b", status.Reference);
        Assert.Equal("2026-03-10", status.CrossedOn);
        Assert.Equal(4_500_000, status.TotalInOre);
    }

    [Fact]
    public void ARefund_BeforeALaterSale_KeepsTheWindowBelowTheThreshold()
    {
        var status = StatusAt(
            [Sale("a", Utc(2026, 1, 10), 3_000_000, Refund(Utc(2026, 2, 2), 1_000_000)), Sale("b", Utc(2026, 3, 10), 2_500_000)],
            NoOutside(), Now);

        Assert.Null(status.Crossing);
    }

    [Theory]
    [InlineData(10, null)]
    [InlineData(15, "b")]
    public void ARefund_OnTheDayOfALaterSale_CountsOnlyWhenItCameFirst(int refundHour, string? crossing)
    {
        var status = StatusAt(
            [Sale("a", Utc(2026, 1, 10), 3_000_000, Refund(Utc(2026, 3, 5, refundHour), 1_000_000)), Sale("b", Utc(2026, 3, 5, 13), 2_500_000)],
            NoOutside(), Now);

        Assert.Equal(crossing, status.Reference);
    }

    [Fact]
    public void ARefund_AtTheSameMomentAsALaterSale_CountsBeforeIt()
    {
        var status = StatusAt(
            [Sale("a", Utc(2026, 1, 10), 3_000_000, Refund(Utc(2026, 3, 5, 13), 1_000_000)), Sale("b", Utc(2026, 3, 5, 13), 2_500_000)],
            NoOutside(), Now);

        Assert.Null(status.Crossing);
    }

    [Fact]
    public void ARefund_DatedBeforeItsSale_CountsFromTheSale()
    {
        var status = StatusAt(
            [Sale("a", Utc(2026, 3, 5), 3_000_000, Refund(Utc(2026, 1, 1), 1_000_000)), Sale("b", Utc(2026, 3, 6), 2_500_000)],
            NoOutside(), Now);

        Assert.Null(status.Crossing);
        Assert.Equal(4_500_000, status.TotalInOre);
    }

    [Fact]
    public void ARefund_TheDayBeforeOutsideSalesStart_CountsBeforeThem()
    {
        // 30 000 kr less 10 000 kr refunded in May, then 29 000 kr outside the shop in June: 49 000 kr.
        var status = StatusAt([Sale("a", Utc(2026, 4, 10), 3_000_000, Refund(Utc(2026, 5, 31), 1_000_000))], Outside(2026, 6, 2_900_000), Now);

        Assert.Null(status.Crossing);
    }

    [Fact]
    public void ARefund_AfterOutsideSalesPassedTheThreshold_DoesNotUndoTheCrossing()
    {
        // 30 000 kr in April and 21 000 kr outside the shop in June pass the threshold on 1 June. The refund is later.
        var status = StatusAt([Sale("a", Utc(2026, 4, 10), 3_000_000, Refund(Utc(2026, 6, 15), 1_000_000))], Outside(2026, 6, 2_100_000), Now);

        Assert.Equal("2026-06-01", status.CrossedOn);
        Assert.Null(status.Reference);
    }

    [Fact]
    public void ARefund_AfterToday_IsNotCountedYet()
    {
        var status = StatusAt([Sale("a", Utc(2026, 9, 10), 1_000_000, Refund(Utc(2026, 10, 20), 400_000))], NoOutside(), Now);

        Assert.Equal(1_000_000, status.TotalInOre);
    }

    [Fact]
    public void Warning_StartsAt40000Kroner()
    {
        int Level(IReadOnlyList<VatSale> sales, Dictionary<DateOnly, long> outside) =>
            VatRegistrationThreshold.WarningLevel(StatusAt(sales, outside, Now).TotalInOre, [80]);

        Assert.Equal(0, Level([Sale("a", Now.AddDays(-1), 3_999_999)], NoOutside()));
        Assert.Equal(80, Level([Sale("a", Now.AddDays(-1), 4_000_000)], NoOutside()));
        Assert.Equal(80, Level([Sale("a", Now.AddDays(-1), 3_000_000)], Outside(2026, 1, 1_000_000)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3_999_999, 0)]
    [InlineData(4_000_000, 80)]
    [InlineData(4_499_999, 80)]
    [InlineData(4_500_000, 90)]
    [InlineData(5_000_000, 90)]
    [InlineData(9_000_000, 90)]
    public void WarningLevel_IsTheHighestPercentReached(long turnover, int level)
    {
        Assert.Equal(level, VatRegistrationThreshold.WarningLevel(turnover, [80, 90]));
        Assert.Equal(level, VatRegistrationThreshold.WarningLevel(turnover, [90, 80]));
    }

    [Fact]
    public void WarningLevel_At100Percent_IsReachedAtTheThreshold_AndNoPercentsGiveZero()
    {
        Assert.Equal(90, VatRegistrationThreshold.WarningLevel(4_999_999, [80, 90, 100]));
        Assert.Equal(100, VatRegistrationThreshold.WarningLevel(5_000_000, [80, 90, 100]));
        Assert.Equal(0, VatRegistrationThreshold.WarningLevel(9_000_000, []));
    }

    [Fact]
    public void ExactlyTheThreshold_IsNotPassingIt()
    {
        List<VatSale> sales = [Sale("a", Now.AddMonths(-3), 4_000_000), Sale("b", Now.AddMonths(-1), 1_000_000)];

        Assert.Null(StatusAt(sales, NoOutside(), Now).Crossing);

        sales.Add(Sale("c", Now.AddDays(-1), 1));
        var status = StatusAt(sales, NoOutside(), Now);
        Assert.Equal("2026-10", status.CrossedMonth);
        Assert.Equal("c", status.Reference);
        Assert.Equal(Now.AddDays(-1), status.CrossedAt);
    }

    [Theory]
    [InlineData(5_000_000, false)]
    [InlineData(5_000_001, true)]
    public void OutsideSalesOfExactlyTheThreshold_DoNotPassIt(long amount, bool crossed)
    {
        var status = StatusAt([], Outside(2026, 5, amount), Now);

        Assert.Equal(crossed ? "2026-05-01" : null, status.CrossedOn);
        Assert.Null(status.Reference);
    }

    [Fact]
    public void TheSaleThatPassesTheThreshold_IsNamed_NotTheOnesAfterIt()
    {
        var status = StatusAt(
            [
                Sale("first", Now.AddMonths(-3), 4_000_000),
                Sale("second", Now.AddMonths(-2), 900_000),
                Sale("after", Now.AddMonths(-1).AddDays(3), 200_000),
                Sale("crossing", Now.AddMonths(-1), 200_000),
            ],
            NoOutside(), Now);

        Assert.Equal("crossing", status.Reference);
        Assert.Equal("2026-09", status.CrossedMonth);
        Assert.Equal(5_300_000, status.TotalInOre);
    }

    [Fact]
    public void SalesOnOneDay_AreCountedInTimeOrder()
    {
        var status = StatusAt(
            [Sale("first", Utc(2026, 5, 1), 4_900_000), Sale("evening", Utc(2026, 6, 3, 18), 50_000), Sale("morning", Utc(2026, 6, 3, 8), 200_000)],
            NoOutside(), Now);

        Assert.Equal("morning", status.Reference);
    }

    [Fact]
    public void SalesAtTheSameMoment_CountTogether_AndTheFirstListedIsNamed()
    {
        var status = StatusAt(
            [Sale("first", Utc(2026, 5, 1), 4_900_000), Sale("small", Utc(2026, 6, 3), 50_000), Sale("big", Utc(2026, 6, 3), 200_000)],
            NoOutside(), Now);

        Assert.Equal("small", status.Reference);
    }

    [Fact]
    public void TheThresholdIsCountedOverTheTwelveMonthsBeforeEachSale()
    {
        // 30 000 kr fourteen months ago has left the window before the later 25 000 kr.
        var status = StatusAt([Sale("old", Now.AddMonths(-14), 3_000_000), Sale("new", Now.AddMonths(-1), 2_500_000)], NoOutside(), Now);

        Assert.Null(status.Crossing);
    }

    [Fact]
    public void ASaleTwelveMonthsEarlier_IsOutsideTheWindow()
    {
        var status = StatusAt([Sale("a", Utc(2025, 9, 10), 3_000_000), Sale("b", Utc(2026, 9, 10), 2_500_000)], NoOutside(), Now);

        Assert.Null(status.Crossing);
    }

    [Fact]
    public void TheWindowOfAnEarlierSale_ReachesTwelveMonthsBackFromIt()
    {
        // Eleven months apart, both in one window, so the second sale passes the threshold.
        var status = StatusAt([Sale("a", Now.AddMonths(-20), 3_000_000), Sale("b", Now.AddMonths(-9), 2_500_000)], NoOutside(), Now);

        Assert.Equal("b", status.Reference);
        Assert.Equal("2026-01", status.CrossedMonth);
        Assert.Equal(2_500_000, status.TotalInOre);
    }

    [Fact]
    public void TheCrossingIsKept_AfterItLeavesTheWindow()
    {
        var crossedAt = Now.AddMonths(-30);
        var status = StatusAt([Sale("big", crossedAt, 5_100_000), Sale("later", Now.AddDays(-1), 10_000)], NoOutside(), Now);

        Assert.Equal("big", status.Reference);
        Assert.Equal("2024-04", status.CrossedMonth);
        Assert.Equal(10_000, status.TotalInOre);
    }

    [Fact]
    public void OutsideSales_CountTowardPassingTheThreshold()
    {
        var status = StatusAt([Sale("sale", Now.AddDays(-1), 200_000)], Outside(2026, 3, 4_900_000), Now);

        Assert.Equal("sale", status.Reference);
        Assert.Equal("2026-10", status.CrossedMonth);
    }

    [Fact]
    public void OutsideSalesThatPassTheThresholdAlone_NameNoSale()
    {
        var status = StatusAt([Sale("sale", Utc(2026, 6, 2), 4_000_000)], Outside(2026, 7, 1_000_001), Now);

        Assert.Equal("2026-07", status.CrossedMonth);
        Assert.Equal("2026-07-01", status.CrossedOn);
        Assert.Null(status.Reference);
        Assert.Null(status.CrossedAt);
    }

    [Fact]
    public void WithinAMonth_TheOutsideSalesAreCountedBeforeTheShopSales()
    {
        // The outside sales bring the window to 49 000 kr, so the first shop sale that month passes it.
        var status = StatusAt(
            [Sale("before", Utc(2026, 5, 10), 4_000_000), Sale("first", Utc(2026, 6, 3), 200_000), Sale("second", Utc(2026, 6, 20), 200_000)],
            Outside(2026, 6, 900_000),
            Now);

        Assert.Equal("first", status.Reference);
    }

    [Fact]
    public void OnTheFirstOfAMonth_TheOutsideSalesAreCountedBeforeASaleThatDay()
    {
        // 49 000 kr in May. The sale on 1 June and the outside sales for June would each pass the threshold, and the
        // outside sales count first.
        var status = StatusAt([Sale("before", Utc(2026, 5, 10), 4_900_000), Sale("first", Utc(2026, 6, 1), 200_000)], Outside(2026, 6, 200_000), Now);

        Assert.Equal("2026-06-01", status.CrossedOn);
        Assert.Null(status.Reference);
    }

    [Fact]
    public void OutsideSalesInTheFuture_DoNotCount()
    {
        var status = StatusAt([], Outside(2026, 11, 9_000_000), Now);

        Assert.Equal(0, status.TotalInOre);
        Assert.Null(status.Crossing);
    }

    [Fact]
    public void SalesAfterUntil_AreNotChecked()
    {
        var status = StatusAt([Sale("a", Utc(2026, 10, 1), 4_000_000), Sale("tomorrow", Utc(2026, 10, 20), 2_000_000)], NoOutside(), Now);

        Assert.Null(status.Crossing);
        Assert.Equal(4_000_000, status.TotalInOre);
    }

    [Theory]
    // Summer time, UTC+2.
    [InlineData(2026, 7, 14, 21, 59, 2026, 7, 14)]
    [InlineData(2026, 7, 14, 22, 0, 2026, 7, 15)]
    // Winter time, UTC+1.
    [InlineData(2026, 1, 14, 22, 59, 2026, 1, 14)]
    [InlineData(2026, 1, 14, 23, 0, 2026, 1, 15)]
    // Clocks go forward at 01:00 UTC on 29 March 2026.
    [InlineData(2026, 3, 28, 22, 59, 2026, 3, 28)]
    [InlineData(2026, 3, 28, 23, 0, 2026, 3, 29)]
    [InlineData(2026, 3, 29, 21, 59, 2026, 3, 29)]
    [InlineData(2026, 3, 29, 22, 0, 2026, 3, 30)]
    // Clocks go back at 01:00 UTC on 25 October 2026.
    [InlineData(2026, 10, 24, 21, 59, 2026, 10, 24)]
    [InlineData(2026, 10, 24, 22, 0, 2026, 10, 25)]
    [InlineData(2026, 10, 25, 22, 59, 2026, 10, 25)]
    [InlineData(2026, 10, 25, 23, 0, 2026, 10, 26)]
    public void DayOf_IsTheOsloDate(int y, int m, int d, int h, int min, int oy, int om, int od)
    {
        Assert.Equal(new DateOnly(oy, om, od), VatRegistrationThreshold.DayOf(Utc(y, m, d, h, min), Zone));
    }

    [Fact]
    public void DayOf_ReadsATimeOfAnyKindAsUtc()
    {
        var unspecified = new DateTime(2026, 7, 14, 22, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateOnly(2026, 7, 15), VatRegistrationThreshold.DayOf(unspecified, Zone));
    }

    [Theory]
    [InlineData(22, "second")]
    [InlineData(21, null)]
    public void TheWindowEdge_FollowsOsloMidnight_AcrossSummerTime(int firstHourUtc, string? crossing)
    {
        // 22:00 UTC on 14 July 2025 is midnight on 15 July in Oslo, the first day of the window ending 14 July 2026.
        var status = StatusAt(
            [Sale("first", Utc(2025, 7, 14, firstHourUtc), 3_000_000), Sale("second", Utc(2026, 7, 14, 10), 2_500_000)],
            NoOutside(), Now);

        Assert.Equal(crossing, status.Reference);
    }

    [Theory]
    [InlineData(23, null)]
    [InlineData(22, "second")]
    public void ASaleJustAfterOsloMidnight_OnTheDayClocksGoBack_CountsOnTheNextDay(int saleHourUtc, string? crossing)
    {
        // 23:00 UTC on 25 October 2026 is midnight on 26 October in Oslo, back on winter time. The window ending
        // 26 October starts 27 October 2025, so the first sale has left it on that day but not on 25 October.
        var status = StatusAt(
            [Sale("first", Utc(2025, 10, 26, 12), 3_000_000), Sale("second", Utc(2026, 10, 25, saleHourUtc), 2_500_000)],
            NoOutside(), Utc(2026, 11, 1));

        Assert.Equal(crossing, status.Reference);
    }

    [Fact]
    public void AMonthKeyThatIsNotTheFirstDay_IsRefused()
    {
        var outside = new Dictionary<DateOnly, long> { [new DateOnly(2026, 6, 2)] = 1 };

        Assert.Throws<ArgumentException>(() => VatRegistrationThreshold.FindCrossing([], outside, Zone, new DateOnly(2026, 10, 1)));
        Assert.Throws<ArgumentException>(() => VatRegistrationThreshold.TurnoverOn(new DateOnly(2026, 10, 1), [], outside, Zone));
    }

    [Fact]
    public void AnEmptySaleOrRefund_IsRefused()
    {
        var day = new DateOnly(2026, 10, 1);
        List<VatSale> emptySale = [Sale("a", Utc(2026, 9, 1), 1), null!];
        List<VatSale> emptyRefund = [new VatSale("a", Utc(2026, 9, 1), 1, [null!])];

        foreach (var sales in new[] { emptySale, emptyRefund })
        {
            Assert.Equal("sales", Assert.Throws<ArgumentException>(() => VatRegistrationThreshold.FindCrossing(sales, NoOutside(), Zone, day)).ParamName);
            Assert.Equal("sales", Assert.Throws<ArgumentException>(() => VatRegistrationThreshold.TurnoverOn(day, sales, NoOutside(), Zone)).ParamName);
        }
    }

    [Fact]
    public void NoSalesAndNoOutsideSales_NeverPassTheThreshold()
    {
        var status = StatusAt([], NoOutside(), Now);

        Assert.Null(status.Crossing);
        Assert.Equal(0, status.TotalInOre);
        Assert.All(status.Turnover.Months, m => Assert.Equal((0, 0), (m.SalesInOre, m.OutsideInOre)));
    }
}
