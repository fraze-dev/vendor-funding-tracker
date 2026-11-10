namespace VendorFunding.Domain;

/// <summary>Amounts still to be booked for one deal when accruing a period. Either can be negative.</summary>
public readonly record struct AccrualTrueUp(decimal PriorPeriodAdjustment, decimal CurrentPeriod);

public static class AccrualCalculator
{
    /// <summary>
    /// Compares what a deal has earned to date with what has already been booked. Working from cumulative
    /// totals means re-running an accrual is safe (nothing new earned = nothing posted), and late data for
    /// a closed period shows up as a prior-period adjustment instead of reopening that period.
    /// </summary>
    /// <param name="earnedThroughPriorPeriods">Earned from the deal start through the end of the previous month.</param>
    /// <param name="earnedThroughPeriod">Earned from the deal start through the end of the month being accrued.</param>
    /// <param name="bookedInPriorPeriods">Already booked for the deal in earlier (closed) periods.</param>
    /// <param name="bookedInPeriod">Already booked for the deal in this period by earlier runs.</param>
    public static AccrualTrueUp Calculate(
        decimal earnedThroughPriorPeriods,
        decimal earnedThroughPeriod,
        decimal bookedInPriorPeriods,
        decimal bookedInPeriod) =>
        new(
            PriorPeriodAdjustment: earnedThroughPriorPeriods - bookedInPriorPeriods,
            CurrentPeriod: earnedThroughPeriod - earnedThroughPriorPeriods - bookedInPeriod);
}
