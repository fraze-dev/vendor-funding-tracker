namespace VendorFunding.Domain;

/// <summary>Calculates vendor funding earned. Pure functions with no I/O, so they're easy to unit test.</summary>
public static class FundingCalculator
{
    public static decimal ScanDownEarned(decimal ratePerUnit, int unitsSold) =>
        RoundToCents(ratePerUnit * unitsSold);

    /// <summary>
    /// Earns a lump-sum amount evenly per day across the deal term (straight-line accrual).
    /// Computed as "earned through window end" minus "earned through the day before window start",
    /// so the amounts for consecutive periods always add up to exactly the contract amount.
    /// </summary>
    public static decimal LumpSumEarned(decimal amount, DateRange term, DateRange window)
    {
        if (term.Intersect(window) is not { } overlap)
        {
            return 0m;
        }

        return EarnedThrough(overlap.End) - EarnedThrough(overlap.Start.AddDays(-1));

        decimal EarnedThrough(DateOnly date)
        {
            if (date < term.Start)
            {
                return 0m;
            }

            var daysElapsed = (date < term.End ? date : term.End).DayNumber - term.Start.DayNumber + 1;
            return RoundToCents(amount * daysElapsed / term.Days);
        }
    }

    /// <summary>Funding a deal has earned in total from its start through <paramref name="date"/>.</summary>
    /// <param name="unitsSoldThroughDate">
    /// Units of the deal's item sold from the deal start through the date. Used by scan-down deals only.
    /// </param>
    public static decimal EarnedThrough(Deal deal, DateOnly date, int unitsSoldThroughDate)
    {
        if (date < deal.StartDate)
        {
            return 0m;
        }

        return deal.Type switch
        {
            DealType.ScanDown => ScanDownEarned(deal.RatePerUnit ?? 0m, unitsSoldThroughDate),
            DealType.LumpSum => LumpSumEarned(
                deal.LumpSumAmount ?? 0m, deal.Term, new DateRange(deal.StartDate, date < deal.EndDate ? date : deal.EndDate)),
            _ => throw new ArgumentOutOfRangeException(nameof(deal), deal.Type, "Unsupported deal type."),
        };
    }

    public static decimal RoundToCents(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
