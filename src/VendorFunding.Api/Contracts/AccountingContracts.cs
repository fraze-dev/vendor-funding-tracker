using VendorFunding.Domain;

namespace VendorFunding.Api.Contracts;

public record PeriodResponse(string Period, PeriodStatus Status, DateTimeOffset? LastAccrualRunAt, DateTimeOffset? ClosedAt)
{
    public static PeriodResponse From(AccountingPeriod p) => new(p.Label, p.Status, p.LastAccrualRunAt, p.ClosedAt);
}

public record AccrualRunResponse(string Period, int EntriesPosted, decimal NetAmountPosted, IReadOnlyList<JournalEntryResponse> Entries);

public record JournalEntryResponse(
    int Id,
    string EntryNumber,
    string Period,
    DateOnly PostingDate,
    JournalEntryType Type,
    string Description,
    int? DealId,
    string? DealNumber,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    IReadOnlyList<JournalLineResponse> Lines)
{
    public static JournalEntryResponse From(JournalEntry e, string period, string? dealNumber) => new(
        e.Id, e.EntryNumber, period, e.PostingDate, e.Type, e.Description, e.DealId, dealNumber,
        e.CreatedAt, e.CreatedBy, e.Lines.Select(JournalLineResponse.From).ToList());
}

public record JournalLineResponse(string Account, string AccountName, decimal Debit, decimal Credit)
{
    public static JournalLineResponse From(JournalLine l) =>
        new(l.AccountNumber, ChartOfAccounts.NameOf(l.AccountNumber), l.Debit, l.Credit);
}

public record TrialBalanceResponse(
    string Period,
    DateOnly AsOf,
    IReadOnlyList<TrialBalanceLine> Accounts,
    decimal TotalDebits,
    decimal TotalCredits,
    bool IsBalanced);

/// <summary>One account's totals through the as-of date. Balance is debits minus credits.</summary>
public record TrialBalanceLine(string Account, string Name, decimal Debits, decimal Credits, decimal Balance);
