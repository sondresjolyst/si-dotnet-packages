# SjolystInnovation.AppKit.VatThreshold

This package holds the rule for the Norwegian VAT registration threshold. Merverdiavgiftsloven § 2-1
first paragraph says a business is to be registered when its turnover "til sammen har oversteget
50.000 kroner i en periode på tolv måneder". Skatteetaten reads this as any period of twelve months,
not a calendar year. Use the package to show turnover against the threshold and to find the sale that
passed it.

It is plain code. It has no database, no endpoints and no services. Your app loads its own sales,
passes them in, and keeps its own alerts and records.

```sh
dotnet add package SjolystInnovation.AppKit.VatThreshold
```

## Get started

```csharp
using SjolystInnovation.AppKit.VatThreshold;

var oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
var today = VatRegistrationThreshold.DayOf(DateTime.UtcNow, oslo);

List<VatSale> sales =
[
    new("order-1", new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), 3_000_000),
    new("order-2", new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc), 2_500_000,
        [new VatRefund(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), 500_000)]),
];
var outsideByMonth = new Dictionary<DateOnly, long> { [new DateOnly(2026, 6, 1)] = 200_000 };

var turnover = VatRegistrationThreshold.TurnoverOn(today, sales, outsideByMonth, oslo);
var crossing = VatRegistrationThreshold.FindCrossing(sales, outsideByMonth, oslo, today);
var level = VatRegistrationThreshold.WarningLevel(turnover.TotalInOre, [80, 90]);
```

Amounts are in øre. `LimitInOre` is 5 000 000.

## How turnover is counted

- Amounts are net turnover covered by the act, without VAT. Merverdiavgiftshåndboken 2-1.3 calls it
  "nettoomsetningen".
- Days are counted in the time zone you pass. A period ends on a day and starts the day after the same
  date twelve months earlier. Both days are included.
- A sale counts on the day it was made. Its amount is the sale less its refunds, never below zero. A
  refund lowers a period only from the moment it was made.
- Sales made outside your app are one figure per month, keyed by the first day of the month. Their day
  is not known, so a month counts in every period it overlaps. This warns early rather than late. Your
  app refuses a month that has not started.

## Turnover

`TurnoverOn` gives the turnover for the period ending on a day, with each month that period overlaps,
oldest first. A month holds the sales inside the period and the whole outside figure for the month.

## Crossing

`FindCrossing` gives the first day where the period ending on it passed the threshold, and the sale
that passed it. Exactly 50 000 kroner has not passed it. On a day the outside sales count first, then
the day's sales in time order. When the outside sales passed the threshold, the sale is empty. A
crossing stays found after it leaves the period, since VAT is owed from it. Days after `until` are not
checked.

## Warning levels

`WarningLevel` gives the highest of your percentages that the turnover has reached, or 0. Reaching
80 % means 40 000 kroner or more.
