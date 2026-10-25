using Xunit;

namespace SjolystInnovation.AppKit.VatThreshold.Tests;

/// <summary>
/// Checks the package against a direct form of the rule, which checks every window sale by sale the way
/// altinnendata-api did before it used the package. Both must give the same turnover and crossing for any input.
/// </summary>
public class ReferenceTests
{
    private static DateOnly DayOf(DateTime utc) => VatRegistrationThreshold.DayOf(utc, Oslo.Zone);

    private static long Kept(VatSale sale, Func<VatRefund, bool> made) =>
        Math.Max(0, sale.AmountInOre - (sale.Refunds ?? []).Where(made).Sum(r => r.AmountInOre));

    private static bool Overlaps(DateOnly month, DateOnly start, DateOnly end) => month <= end && month.AddMonths(1) > start;

    private static (DateOnly Day, VatSale? Sale)? DirectCrossing(IReadOnlyList<VatSale> sales, IReadOnlyDictionary<DateOnly, long> outside, DateOnly today)
    {
        var dated = sales.Select(s => (Day: DayOf(s.AtUtc), Sale: s)).ToList();
        var days = dated.Select(s => s.Day).Concat(outside.Keys).Where(d => d <= today).Distinct().Order();

        foreach (var day in days)
        {
            var start = VatRegistrationThreshold.WindowStart(day);
            var outsideInWindow = outside.Where(m => Overlaps(m.Key, start, day)).Sum(m => m.Value);
            var beforeDay = outsideInWindow
                + dated.Where(s => s.Day >= start && s.Day < day).Sum(s => Kept(s.Sale, r => DayOf(r.AtUtc) < day));
            if (beforeDay > VatRegistrationThreshold.LimitInOre) return (day, null);

            foreach (var sale in dated.Where(s => s.Day == day).Select(s => s.Sale).OrderBy(s => s.AtUtc))
            {
                var running = outsideInWindow
                    + dated.Where(s => s.Day >= start && s.Sale.AtUtc <= sale.AtUtc).Sum(s => Kept(s.Sale, r => r.AtUtc <= sale.AtUtc));
                if (running > VatRegistrationThreshold.LimitInOre) return (day, sale);
            }
        }

        return null;
    }

    private static List<VatTurnoverMonth> DirectMonths(IReadOnlyList<VatSale> sales, IReadOnlyDictionary<DateOnly, long> outside, DateOnly today)
    {
        var start = VatRegistrationThreshold.WindowStart(today);
        var shopByMonth = sales
            .Where(s => DayOf(s.AtUtc) >= start && DayOf(s.AtUtc) <= today)
            .GroupBy(s => new DateOnly(DayOf(s.AtUtc).Year, DayOf(s.AtUtc).Month, 1))
            .ToDictionary(g => g.Key, g => g.Sum(s => Kept(s, r => DayOf(r.AtUtc) <= today)));
        var months = new List<VatTurnoverMonth>();
        for (var month = new DateOnly(start.Year, start.Month, 1); month <= today; month = month.AddMonths(1))
            months.Add(new VatTurnoverMonth(month, shopByMonth.GetValueOrDefault(month), outside.GetValueOrDefault(month)));
        return months;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ThePackage_GivesTheSameAnswerAsTheDirectRule(int seed)
    {
        var random = new Random(seed);
        var origin = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var crossings = 0;

        for (var round = 0; round < 400; round++)
        {
            // Few distinct moments, so sales and refunds often share a moment or a day.
            DateTime Moment() => origin.AddHours(random.Next(0, 3 * 365 * 4) * 6 + random.Next(0, 2) * 23);
            var sales = new List<VatSale>();
            for (var i = random.Next(0, 25); i > 0; i--)
            {
                var at = Moment();
                var refunds = new List<VatRefund>();
                for (var r = random.Next(0, 3); r > 0; r--)
                {
                    // Some refunds are dated before their sale, a few minutes or a few days, the rest after it.
                    var refundAt = random.Next(0, 8) switch
                    {
                        0 => at.AddDays(-random.Next(0, 5)),
                        1 => at.AddMinutes(-random.Next(1, 60)),
                        2 => at.AddMinutes(random.Next(0, 120)),
                        _ => at.AddHours(random.Next(0, 24 * 200)),
                    };
                    refunds.Add(new VatRefund(refundAt, random.Next(0, 1_500_000)));
                }
                sales.Add(new VatSale($"s{i}", at, random.Next(0, 2_000_000), refunds));
            }
            var outside = new Dictionary<DateOnly, long>();
            for (var m = random.Next(0, 4); m > 0; m--)
                outside[new DateOnly(2024, 1, 1).AddMonths(random.Next(0, 38))] = random.Next(0, 3_000_000);
            var today = DateOnly.FromDateTime(origin).AddDays(random.Next(0, 3 * 365));

            var expected = DirectCrossing(sales, outside, today);
            var actual = VatRegistrationThreshold.FindCrossing(sales, outside, Oslo.Zone, today);

            Assert.Equal(expected?.Day, actual?.Day);
            Assert.Same(expected?.Sale, actual?.Sale);
            var months = DirectMonths(sales, outside, today);
            var turnover = VatRegistrationThreshold.TurnoverOn(today, sales, outside, Oslo.Zone);
            Assert.Equal(months, turnover.Months);
            Assert.Equal(months.Sum(m => m.SalesInOre + m.OutsideInOre), turnover.TotalInOre);
            if (actual is not null) crossings++;
        }

        // The cases must both pass and stay under the threshold, or the comparison proves little.
        Assert.InRange(crossings, 40, 360);
    }

    [Fact]
    public void ManySales_AreCheckedQuickly()
    {
        var random = new Random(7);
        var origin = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sales = Enumerable.Range(0, 200_000)
            .Select(i => new VatSale($"s{i}", origin.AddMinutes(random.Next(0, 6 * 365 * 24 * 60)), 10,
                [new VatRefund(origin.AddMinutes(random.Next(0, 6 * 365 * 24 * 60)), 5)]))
            .ToList();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var crossing = VatRegistrationThreshold.FindCrossing(sales, new Dictionary<DateOnly, long>(), Oslo.Zone, new DateOnly(2026, 1, 1));

        Assert.Null(crossing);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(20));
    }
}
