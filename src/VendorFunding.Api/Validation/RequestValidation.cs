using VendorFunding.Contracts;
using VendorFunding.Domain;

namespace VendorFunding.Api.Validation;

/// <summary>
/// Validation for the shared request contracts. It lives in the API, not the contracts library, so the
/// Blazor client shares the request shapes without depending on server-side rules.
/// </summary>
public static class RequestValidation
{
    public static Dictionary<string, string[]> Validate(this CreateVendorRequest request)
    {
        var errors = new ValidationErrors();
        errors.RequiredMaxLength(nameof(request.VendorNumber), request.VendorNumber, 20);
        errors.RequiredMaxLength(nameof(request.Name), request.Name, 200);
        return errors.ToDictionary();
    }

    public static Dictionary<string, string[]> Validate(this CreateDealRequest request)
    {
        var errors = new ValidationErrors();
        errors.RequiredMaxLength(nameof(request.DealNumber), request.DealNumber, 20);
        errors.RequiredMaxLength(nameof(request.Description), request.Description, 200);

        if (request.EndDate < request.StartDate)
        {
            errors.Add(nameof(request.EndDate), "End date must be on or after the start date.");
        }

        switch (request.Type)
        {
            case DealType.ScanDown:
                errors.RequiredMaxLength(nameof(request.Sku), request.Sku, 20);
                if (request.RatePerUnit is not > 0)
                {
                    errors.Add(nameof(request.RatePerUnit), "Scan-down deals need a rate per unit greater than zero.");
                }
                if (request.LumpSumAmount is not null)
                {
                    errors.Add(nameof(request.LumpSumAmount), "Scan-down deals can't have a lump-sum amount.");
                }
                break;

            case DealType.LumpSum:
                if (request.LumpSumAmount is not > 0)
                {
                    errors.Add(nameof(request.LumpSumAmount), "Lump-sum deals need an amount greater than zero.");
                }
                if (request.RatePerUnit is not null)
                {
                    errors.Add(nameof(request.RatePerUnit), "Lump-sum deals can't have a rate per unit.");
                }
                break;

            default:
                errors.Add(nameof(request.Type), $"Unsupported deal type '{request.Type}'.");
                break;
        }

        return errors.ToDictionary();
    }

    public static Dictionary<string, string[]> Validate(this RecordPaymentRequest request)
    {
        var errors = new ValidationErrors();
        errors.RequiredMaxLength(nameof(request.Reference), request.Reference, 30);
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
        {
            errors.Add(nameof(request.Amount), "Amount must be greater than zero, with at most two decimal places.");
        }

        if (request.Memo is { Length: > 200 })
        {
            errors.Add(nameof(request.Memo), "Memo must be 200 characters or fewer.");
        }

        return errors.ToDictionary();
    }
}

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
