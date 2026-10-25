namespace SjolystInnovation.AppKit.VatThreshold;

/// <summary>
/// One sale: when it was made, in UTC, what it came to in øre, and the refunds of it. <paramref name="Reference"/> is
/// the app's own name for the sale, such as an order or invoice number.
/// </summary>
public sealed record VatSale(string Reference, DateTime AtUtc, long AmountInOre, IReadOnlyList<VatRefund>? Refunds = null);

/// <summary>One refund of a sale: when it was made, in UTC, and how much in øre.</summary>
public sealed record VatRefund(DateTime AtUtc, long AmountInOre);

/// <summary>
/// The first day the period of twelve months ending on it passed the threshold, and the sale that passed it. The sale
/// is empty when sales entered per month passed it, since their day is not known.
/// </summary>
public sealed record VatCrossing(DateOnly Day, VatSale? Sale);

/// <summary>Turnover for the period of twelve months ending on one day, with the months the period overlaps, oldest first.</summary>
public sealed record VatTurnover(long TotalInOre, IReadOnlyList<VatTurnoverMonth> Months);

/// <summary>
/// One month of a period. <paramref name="SalesInOre"/> holds the dated sales made in the month and inside the period,
/// less their refunds. <paramref name="OutsideInOre"/> holds the whole figure entered for the month.
/// <paramref name="Month"/> is the first day of the month.
/// </summary>
public sealed record VatTurnoverMonth(DateOnly Month, long SalesInOre, long OutsideInOre);
