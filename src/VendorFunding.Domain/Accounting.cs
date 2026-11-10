using System.Globalization;

namespace VendorFunding.Domain;

public static class ChartOfAccounts
{
    public const string VendorFundingReceivable = "1310";
    public const string CogsVendorAllowances = "5010";

    public static string NameOf(string accountNumber) => accountNumber switch
    {
        VendorFundingReceivable => "Vendor Funding Receivable",
        CogsVendorAllowances => "COGS - Vendor Allowances",
        _ => "Unknown account",
    };
}

/// <summary>A request broke an accounting rule, such as posting to a closed period. The API returns 409 Conflict.</summary>
public sealed class AccountingRuleException(string message) : Exception(message);

public enum PeriodStatus
{
    Open,
    Closed,
}

/// <summary>
/// A monthly accounting period. Periods open and close in order, one at a time: entries can only be
/// posted to the single open period, and closing it lets the next month open.
/// </summary>
public class AccountingPeriod
{
    public int Id { get; set; }

    /// <summary>First day of the month.</summary>
    public DateOnly StartDate { get; set; }

    public PeriodStatus Status { get; set; } = PeriodStatus.Open;
    public DateTimeOffset? LastAccrualRunAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Concurrency token, so two accrual runs (or a run and a close) on one period can't both succeed.</summary>
    public byte[] RowVersion { get; set; } = [];

    public DateRange Range => new(StartDate, StartDate.AddMonths(1).AddDays(-1));
    public string Label => StartDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}

public enum JournalEntryType
{
    /// <summary>Funding earned during the period itself.</summary>
    Accrual,

    /// <summary>Funding for an already-closed period, e.g. from sales reported late. Booked in the open period.</summary>
    PriorPeriodAdjustment,
}

/// <summary>A posted journal entry. Posted entries are never changed or deleted; corrections are new entries.</summary>
public class JournalEntry
{
    public int Id { get; set; }
    public int AccountingPeriodId { get; set; }
    public AccountingPeriod? AccountingPeriod { get; set; }
    public DateOnly PostingDate { get; set; }
    public JournalEntryType Type { get; set; }
    public required string Description { get; set; }
    public int? DealId { get; set; }
    public Deal? Deal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public List<JournalLine> Lines { get; set; } = [];

    public string EntryNumber => $"JE-{Id:D6}";

    /// <summary>
    /// Books funding for one deal. A positive amount is Dr Vendor Funding Receivable / Cr COGS - Vendor Allowances;
    /// a negative amount (funding earned went down, e.g. returns) books the reverse.
    /// </summary>
    public static JournalEntry ForDealFunding(
        AccountingPeriod period, Deal deal, JournalEntryType type, decimal amount, string createdBy, DateTimeOffset createdAt)
    {
        if (amount == 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A journal entry needs a non-zero amount.");
        }

        var (debitAccount, creditAccount) = amount > 0
            ? (ChartOfAccounts.VendorFundingReceivable, ChartOfAccounts.CogsVendorAllowances)
            : (ChartOfAccounts.CogsVendorAllowances, ChartOfAccounts.VendorFundingReceivable);
        var value = FundingCalculator.RoundToCents(Math.Abs(amount));
        var label = type == JournalEntryType.Accrual ? "Funding accrual" : "Prior-period adjustment";

        var entry = new JournalEntry
        {
            AccountingPeriod = period,
            PostingDate = period.Range.End,
            Type = type,
            Description = $"{label}: {deal.DealNumber} {deal.Description}",
            DealId = deal.Id,
            CreatedAt = createdAt,
            CreatedBy = createdBy,
            Lines =
            [
                new JournalLine { AccountNumber = debitAccount, Debit = value },
                new JournalLine { AccountNumber = creditAccount, Credit = value },
            ],
        };
        EnsureBalanced(entry.Lines);
        return entry;
    }

    /// <summary>Double-entry rules: each line is a positive debit or a positive credit (not both), and debits equal credits.</summary>
    public static void EnsureBalanced(IReadOnlyCollection<JournalLine> lines)
    {
        if (lines.Count < 2)
        {
            throw new AccountingRuleException("A journal entry needs at least two lines.");
        }

        foreach (var line in lines)
        {
            if (line.Debit < 0 || line.Credit < 0 || (line.Debit > 0) == (line.Credit > 0))
            {
                throw new AccountingRuleException(
                    $"Each line needs either a positive debit or a positive credit, not both (account {line.AccountNumber}).");
            }
        }

        var debits = lines.Sum(l => l.Debit);
        var credits = lines.Sum(l => l.Credit);
        if (debits != credits)
        {
            throw new AccountingRuleException($"Debits ({debits:N2}) must equal credits ({credits:N2}).");
        }
    }
}

public class JournalLine
{
    public int Id { get; set; }
    public int JournalEntryId { get; set; }
    public required string AccountNumber { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}
