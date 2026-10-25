namespace SjolystInnovation.AppKit.VatThreshold;

/// <summary>
/// The VAT registration threshold in merverdiavgiftsloven § 2-1 first paragraph: a business is to be registered when
/// turnover and withdrawals covered by the act "til sammen har oversteget 50.000 kroner i en periode på tolv måneder".
/// Skatteetaten reads the period as "hvilken som helst periode på 12 måneder", not a calendar year, and VAT is owed
/// "fra og med det tidspunkt" the turnover passes the threshold. So every period of twelve months is checked, one
/// for each day it can end on, and the threshold is passed only when turnover is above it, not at it.
/// </summary>
/// <remarks>
/// <para>
/// Days are counted in the time zone the app passes, such as Europe/Oslo. A period ends on a day and starts the day
/// after the same date twelve months earlier, both days included. A sale counts on the day it was made. Its amount is
/// the sale less its refunds, never below zero, and a refund lowers a period only from the moment it was made.
/// </para>
/// <para>
/// Sales outside the app are entered as one figure per month. Their day is not known, so a month counts in every
/// period it overlaps. This warns early rather than late. The keys are the first day of each month. Refusing a month
/// that has not started is for the app to do.
/// </para>
/// </remarks>
public static class VatRegistrationThreshold
{
    /// <summary>The threshold, 50 000 kroner.</summary>
    public const long LimitInOre = 5_000_000;

    /// <summary>The length of the period, in months.</summary>
    public const int WindowMonths = 12;

    /// <summary>The date <paramref name="utc"/> falls on in <paramref name="zone"/>. The time is read as UTC whatever its kind.</summary>
    public static DateOnly DayOf(DateTime utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
    }

    /// <summary>
    /// The first day of the period of twelve months that ends on <paramref name="end"/>, both days included. It is the
    /// day after the same date twelve months earlier. When that date does not exist, the month's last day is used.
    /// </summary>
    public static DateOnly WindowStart(DateOnly end) => end.AddMonths(-WindowMonths).AddDays(1);

    /// <summary>
    /// Turnover for the period ending on <paramref name="end"/>: the sales made inside it less the refunds made up to
    /// and including that day, and every month of <paramref name="outsideByMonth"/> the period overlaps. The months
    /// list each month the period overlaps, oldest first, with zero for a month without sales.
    /// </summary>
    public static VatTurnover TurnoverOn(
        DateOnly end,
        IReadOnlyList<VatSale> sales,
        IReadOnlyDictionary<DateOnly, long> outsideByMonth,
        TimeZoneInfo zone)
    {
        CheckSales(sales);
        CheckMonths(outsideByMonth);
        ArgumentNullException.ThrowIfNull(zone);

        var start = WindowStart(end);
        var salesByMonth = new Dictionary<DateOnly, long>();
        foreach (var sale in sales)
        {
            var day = DayOf(sale.AtUtc, zone);
            if (day < start || day > end) continue;
            var month = FirstOfMonth(day);
            salesByMonth[month] = salesByMonth.GetValueOrDefault(month) + Kept(sale, r => DayOf(r.AtUtc, zone) <= end);
        }

        var months = new List<VatTurnoverMonth>();
        long total = 0;
        for (var month = FirstOfMonth(start); month <= end; month = month.AddMonths(1))
        {
            var row = new VatTurnoverMonth(month, salesByMonth.GetValueOrDefault(month), outsideByMonth.GetValueOrDefault(month));
            total += row.SalesInOre + row.OutsideInOre;
            months.Add(row);
        }

        return new VatTurnover(total, months);
    }

    /// <summary>
    /// The first day, up to and including <paramref name="until"/>, where the period ending on it passed the threshold,
    /// and the sale that passed it. The crossing stays found after it leaves the period.
    /// </summary>
    /// <remarks>
    /// A period only grows on a day a sale is made or a month of outside sales starts, so those are the days checked.
    /// On a day the outside sales are counted first, since their day is not known, and the sale is empty when they
    /// passed the threshold. Then the day's sales are counted in time order. Sales made at the same moment count
    /// together, and the first of them in <paramref name="sales"/> is named. The days are scanned once in date order,
    /// so the work grows with the number of sales and refunds, not with days times sales.
    /// </remarks>
    public static VatCrossing? FindCrossing(
        IReadOnlyList<VatSale> sales,
        IReadOnlyDictionary<DateOnly, long> outsideByMonth,
        TimeZoneInfo zone,
        DateOnly until)
    {
        CheckSales(sales);
        CheckMonths(outsideByMonth);
        ArgumentNullException.ThrowIfNull(zone);

        var count = sales.Count;
        var saleDays = new DateOnly[count];
        var events = new List<Change>(count);
        for (var i = 0; i < count; i++)
        {
            var sale = sales[i];
            saleDays[i] = DayOf(sale.AtUtc, zone);
            events.Add(new Change(saleDays[i], sale.AtUtc, IsRefund: false, i, sale.AmountInOre));
            // A refund never counts before its sale, so a refund dated earlier counts from the sale.
            foreach (var refund in sale.Refunds ?? [])
            {
                var day = DayOf(refund.AtUtc, zone);
                events.Add(new Change(
                    day > saleDays[i] ? day : saleDays[i],
                    refund.AtUtc > sale.AtUtc ? refund.AtUtc : sale.AtUtc,
                    IsRefund: true, i, refund.AmountInOre));
            }
        }
        events.Sort(Change.Compare);

        var order = Enumerable.Range(0, count)
            .OrderBy(i => saleDays[i]).ThenBy(i => sales[i].AtUtc).ThenBy(i => i)
            .ToArray();
        var months = outsideByMonth.OrderBy(m => m.Key).ToArray();
        var days = saleDays.Concat(outsideByMonth.Keys).Where(d => d <= until).Distinct().Order();

        var kept = new long[count];
        var refunded = new long[count];
        var counted = new bool[count];
        var left = new bool[count];
        long inWindow = 0;
        long outside = 0;
        int nextEvent = 0, nextLeaving = 0, nextOnDay = 0, nextMonth = 0, nextMonthLeaving = 0;

        void Apply(Change change)
        {
            var i = change.Sale;
            if (change.IsRefund)
            {
                refunded[i] += change.AmountInOre;
                var now = Math.Max(0, sales[i].AmountInOre - refunded[i]);
                if (counted[i] && !left[i]) inWindow += now - kept[i];
                kept[i] = now;
            }
            else
            {
                kept[i] = Math.Max(0, change.AmountInOre - refunded[i]);
                counted[i] = true;
                if (!left[i]) inWindow += kept[i];
            }
        }

        foreach (var day in days)
        {
            var start = WindowStart(day);

            // Everything up to the end of the day before.
            while (nextEvent < events.Count && events[nextEvent].Day < day) Apply(events[nextEvent++]);

            // Sales made before the period started leave it, with what was left of them.
            while (nextLeaving < count && saleDays[order[nextLeaving]] < start)
            {
                var i = order[nextLeaving++];
                if (counted[i]) inWindow -= kept[i];
                left[i] = true;
            }

            // A month of outside sales counts from its first day until the period no longer overlaps it.
            while (nextMonth < months.Length && months[nextMonth].Key <= day) outside += months[nextMonth++].Value;
            while (nextMonthLeaving < nextMonth && months[nextMonthLeaving].Key.AddMonths(1) <= start)
                outside -= months[nextMonthLeaving++].Value;

            if (outside + inWindow > LimitInOre) return new VatCrossing(day, null);

            while (nextOnDay < count && saleDays[order[nextOnDay]] < day) nextOnDay++;
            while (nextOnDay < count && saleDays[order[nextOnDay]] == day)
            {
                var first = sales[order[nextOnDay]];
                while (nextOnDay < count && saleDays[order[nextOnDay]] == day && sales[order[nextOnDay]].AtUtc == first.AtUtc)
                    nextOnDay++;
                while (nextEvent < events.Count && events[nextEvent].Day == day && events[nextEvent].At <= first.AtUtc)
                    Apply(events[nextEvent++]);

                if (outside + inWindow > LimitInOre) return new VatCrossing(day, first);
            }
        }

        return null;
    }

    /// <summary>
    /// The highest of <paramref name="percents"/> that <paramref name="turnoverInOre"/> has reached as a share of the
    /// threshold, or 0 when it has reached none. Reaching a share includes being exactly at it.
    /// </summary>
    public static int WarningLevel(long turnoverInOre, IReadOnlyList<int> percents)
    {
        ArgumentNullException.ThrowIfNull(percents);
        var level = 0;
        foreach (var percent in percents)
            if (percent > level && turnoverInOre * 100 >= LimitInOre * percent) level = percent;
        return level;
    }

    /// <summary>What is left of <paramref name="sale"/> once the refunds <paramref name="made"/> picks are taken off, never below zero.</summary>
    private static long Kept(VatSale sale, Func<VatRefund, bool> made) =>
        Math.Max(0, sale.AmountInOre - (sale.Refunds ?? []).Where(made).Sum(r => r.AmountInOre));

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    private static void CheckSales(IReadOnlyList<VatSale> sales)
    {
        ArgumentNullException.ThrowIfNull(sales);
        foreach (var sale in sales)
        {
            if (sale is null) throw new ArgumentException("The sales hold an empty sale.", nameof(sales));
            if (sale.Refunds?.Any(r => r is null) == true)
                throw new ArgumentException($"The sale {sale.Reference} holds an empty refund.", nameof(sales));
        }
    }

    private static void CheckMonths(IReadOnlyDictionary<DateOnly, long> outsideByMonth)
    {
        ArgumentNullException.ThrowIfNull(outsideByMonth);
        foreach (var month in outsideByMonth.Keys)
            if (month.Day != 1)
                throw new ArgumentException($"The month {month:yyyy-MM-dd} is not the first day of a month.", nameof(outsideByMonth));
    }

    /// <summary>A sale or a refund as it changes the turnover, at the day and time it counts from.</summary>
    private readonly record struct Change(DateOnly Day, DateTime At, bool IsRefund, int Sale, long AmountInOre)
    {
        /// <summary>By day, then time, with a sale before a refund at the same moment.</summary>
        public static int Compare(Change a, Change b)
        {
            var byDay = a.Day.CompareTo(b.Day);
            if (byDay != 0) return byDay;
            var byTime = a.At.CompareTo(b.At);
            if (byTime != 0) return byTime;
            var byKind = a.IsRefund.CompareTo(b.IsRefund);
            return byKind != 0 ? byKind : a.Sale.CompareTo(b.Sale);
        }
    }
}
