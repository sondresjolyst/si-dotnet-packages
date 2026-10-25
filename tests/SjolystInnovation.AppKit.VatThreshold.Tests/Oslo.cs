namespace SjolystInnovation.AppKit.VatThreshold.Tests;

/// <summary>The Oslo zone and the status an app reads for a moment, as altinnendata-api does.</summary>
internal static class Oslo
{
    public static readonly TimeZoneInfo Zone = Load();

    private static TimeZoneInfo Load()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    public sealed record Status(VatTurnover Turnover, VatCrossing? Crossing)
    {
        public long TotalInOre => Turnover.TotalInOre;
        public string? CrossedOn => Crossing?.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        public string? CrossedMonth => Crossing?.Day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        public string? Reference => Crossing?.Sale?.Reference;
        public DateTime? CrossedAt => Crossing?.Sale?.AtUtc;
        public VatTurnoverMonth Month(int year, int month) => Turnover.Months.Single(m => m.Month == new DateOnly(year, month, 1));
    }

    /// <summary>The turnover for the period ending today and the crossing up to today, today being the Oslo date of <paramref name="now"/>.</summary>
    public static Status StatusAt(IReadOnlyList<VatSale> sales, IReadOnlyDictionary<DateOnly, long> outside, DateTime now)
    {
        var today = VatRegistrationThreshold.DayOf(now, Zone);
        return new Status(
            VatRegistrationThreshold.TurnoverOn(today, sales, outside, Zone),
            VatRegistrationThreshold.FindCrossing(sales, outside, Zone, today));
    }
}
