using VendorFunding.Domain;

namespace VendorFunding.Api.Contracts;

public record CreateVendorRequest(string VendorNumber, string Name)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new ValidationErrors();
        errors.RequiredMaxLength(nameof(VendorNumber), VendorNumber, 20);
        errors.RequiredMaxLength(nameof(Name), Name, 200);
        return errors.ToDictionary();
    }
}

public record CreateDealRequest(
    string DealNumber,
    string Description,
    DealType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Sku,
    decimal? RatePerUnit,
    decimal? LumpSumAmount)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new ValidationErrors();
        errors.RequiredMaxLength(nameof(DealNumber), DealNumber, 20);
        errors.RequiredMaxLength(nameof(Description), Description, 200);

        if (EndDate < StartDate)
        {
            errors.Add(nameof(EndDate), "End date must be on or after the start date.");
        }

        switch (Type)
        {
            case DealType.ScanDown:
                errors.RequiredMaxLength(nameof(Sku), Sku, 20);
                if (RatePerUnit is not > 0)
                {
                    errors.Add(nameof(RatePerUnit), "Scan-down deals need a rate per unit greater than zero.");
                }
                if (LumpSumAmount is not null)
                {
                    errors.Add(nameof(LumpSumAmount), "Scan-down deals can't have a lump-sum amount.");
                }
                break;

            case DealType.LumpSum:
                if (LumpSumAmount is not > 0)
                {
                    errors.Add(nameof(LumpSumAmount), "Lump-sum deals need an amount greater than zero.");
                }
                if (RatePerUnit is not null)
                {
                    errors.Add(nameof(RatePerUnit), "Lump-sum deals can't have a rate per unit.");
                }
                break;

            default:
                errors.Add(nameof(Type), $"Unsupported deal type '{Type}'.");
                break;
        }

        return errors.ToDictionary();
    }
}

public record SalesRecordRequest(string StoreNumber, string Sku, DateOnly SaleDate, int UnitsSold, decimal NetSales);

/// <summary>Collects field-level errors in the shape <see cref="TypedResults.ValidationProblem"/> expects.</summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
        }

        messages.Add(message);
    }

    public void RequiredMaxLength(string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, $"{field} is required.");
        }
        else if (value.Length > maxLength)
        {
            Add(field, $"{field} must be {maxLength} characters or fewer.");
        }
    }

    public Dictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
}
