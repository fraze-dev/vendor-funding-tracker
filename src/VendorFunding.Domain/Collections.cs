namespace VendorFunding.Domain;

/// <summary>Money a vendor paid against the funding it owes. Each payment is booked as a cash-receipt journal entry.</summary>
public class VendorPayment
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    /// <summary>The vendor's payment reference, such as an ACH trace or check number. Unique per vendor.</summary>
    public required string Reference { get; set; }

    public DateOnly ReceivedDate { get; set; }
    public decimal Amount { get; set; }
    public string? Memo { get; set; }
    public int JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>A change to a vendor's receivable: positive when funding is accrued, negative when it's paid or reduced.</summary>
public readonly record struct ReceivableTransaction(DateOnly Date, decimal Amount);

/// <summary>Open receivable grouped by how long it has been outstanding.</summary>
public readonly record struct AgingBuckets(decimal Current, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public decimal Total => Current + Days31To60 + Days61To90 + Over90;
}

/// <param name="Open">What the vendor still owes, by age.</param>
/// <param name="UnappliedCredit">Payments (or reductions) beyond everything owed, e.g. an overpayment.</param>
public readonly record struct AgingResult(AgingBuckets Open, decimal UnappliedCredit);

public static class ReceivableAging
{
    /// <summary>
    /// Ages a vendor's receivable as of a date. Credits (payments and negative true-ups) settle the oldest amounts
    /// first (FIFO), the usual convention when a payment isn't tied to specific invoices. Each remaining amount is
    /// aged from its posting date: 0-30 days is current, then 31-60, 61-90 and over 90.
    /// </summary>
    public static AgingResult Calculate(IEnumerable<ReceivableTransaction> transactions, DateOnly asOf)
    {
        var relevant = transactions.Where(t => t.Date <= asOf).ToList();
        var credits = -relevant.Where(t => t.Amount < 0).Sum(t => t.Amount);

        decimal current = 0, days31To60 = 0, days61To90 = 0, over90 = 0;
        foreach (var charge in relevant.Where(t => t.Amount > 0).OrderBy(t => t.Date))
        {
            var applied = Math.Min(charge.Amount, credits);
            credits -= applied;
            var open = charge.Amount - applied;
            if (open == 0)
            {
                continue;
            }

            switch (asOf.DayNumber - charge.Date.DayNumber)
            {
                case <= 30: current += open; break;
                case <= 60: days31To60 += open; break;
                case <= 90: days61To90 += open; break;
                default: over90 += open; break;
            }
        }

        return new AgingResult(new AgingBuckets(current, days31To60, days61To90, over90), credits);
    }
}
