using System.Globalization;

namespace VendorFunding.Domain;

public enum SalesImportStatus
{
    /// <summary>The file is stored and waiting to be processed.</summary>
    Received,

    /// <summary>The file was parsed; valid rows are queued in batches for loading.</summary>
    Loading,

    /// <summary>Every row was loaded.</summary>
    Completed,

    /// <summary>Valid rows were loaded; some rows were rejected (see the rejects file).</summary>
    CompletedWithErrors,

    /// <summary>The same file content was already imported, so nothing was loaded again.</summary>
    Duplicate,

    /// <summary>The file couldn't be processed at all, e.g. a missing or wrong header row.</summary>
    Failed,
}

/// <summary>One sales file received from a store, and what happened to it.</summary>
public class SalesImport
{
    public Guid Id { get; set; }

    /// <summary>The file name the sender used.</summary>
    public required string FileName { get; set; }

    /// <summary>Where the file is stored, relative to the inbox container.</summary>
    public required string BlobName { get; set; }

    /// <summary>SHA-256 of the file content, used to detect a file that's sent twice.</summary>
    public string? ContentHash { get; set; }

    public SalesImportStatus Status { get; set; } = SalesImportStatus.Received;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TotalRows { get; set; }
    public int RejectedRows { get; set; }
    public int LoadedRows { get; set; }
    public int TotalBatches { get; set; }

    /// <summary>Rejected rows and why, relative to the rejects container.</summary>
    public string? RejectsBlobName { get; set; }

    public string? Error { get; set; }

    public bool IsFinished => Status is SalesImportStatus.Completed or SalesImportStatus.CompletedWithErrors
        or SalesImportStatus.Duplicate or SalesImportStatus.Failed;
}

/// <summary>Records that a batch was loaded, so a redelivered queue message doesn't load it twice.</summary>
public class SalesImportBatch
{
    public Guid ImportId { get; set; }
    public int BatchNumber { get; set; }
    public int RowCount { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
}

public record ParsedSalesRow(string StoreNumber, string Sku, DateOnly SaleDate, int UnitsSold, decimal NetSales);

public record SalesFileRowError(int LineNumber, string Line, string Error);

public record SalesFileParseResult(IReadOnlyList<ParsedSalesRow> Rows, IReadOnlyList<SalesFileRowError> Errors, string? FileError)
{
    public static SalesFileParseResult Invalid(string fileError) => new([], [], fileError);
}

/// <summary>
/// Parses a store's daily sales CSV. Bad rows are collected with a reason instead of failing the whole file,
/// so one typo doesn't block a store's sales. Values can't contain commas or quotes.
/// </summary>
public static class SalesFileParser
{
    public const string ExpectedHeader = "store_number,sku,sale_date,units_sold,net_sales";
    public const int MaxStoreNumberLength = 10;
    public const int MaxSkuLength = 20;

    public static SalesFileParseResult Parse(TextReader reader)
    {
        var header = reader.ReadLine();
        if (header is null)
        {
            return SalesFileParseResult.Invalid("The file is empty.");
        }

        if (!string.Equals(header.Trim().TrimStart('﻿'), ExpectedHeader, StringComparison.OrdinalIgnoreCase))
        {
            return SalesFileParseResult.Invalid($"The first line must be the header '{ExpectedHeader}'.");
        }

        var rows = new List<ParsedSalesRow>();
        var errors = new List<SalesFileRowError>();
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseRow(line, out var row, out var error))
            {
                rows.Add(row);
            }
            else
            {
                errors.Add(new SalesFileRowError(lineNumber, line, error));
            }
        }

        return new SalesFileParseResult(rows, errors, null);
    }

    private static bool TryParseRow(string line, out ParsedSalesRow row, out string error)
    {
        row = null!;
        var fields = line.Split(',');
        if (fields.Length != 5)
        {
            error = $"Expected 5 values but found {fields.Length}.";
            return false;
        }

        var (store, sku) = (fields[0].Trim(), fields[1].Trim());
        if (store.Length is 0 or > MaxStoreNumberLength)
        {
            error = $"store_number is required and can be at most {MaxStoreNumberLength} characters.";
            return false;
        }

        if (sku.Length is 0 or > MaxSkuLength)
        {
            error = $"sku is required and can be at most {MaxSkuLength} characters.";
            return false;
        }

        if (!DateOnly.TryParseExact(fields[2].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            error = "sale_date must be a date in the format yyyy-MM-dd.";
            return false;
        }

        if (!int.TryParse(fields[3].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var units))
        {
            error = "units_sold must be a whole number (negative for net returns).";
            return false;
        }

        if (!decimal.TryParse(fields[4].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var netSales) || decimal.Round(netSales, 2) != netSales)
        {
            error = "net_sales must be an amount with at most two decimal places.";
            return false;
        }

        row = new ParsedSalesRow(store, sku, date, units, netSales);
        error = "";
        return true;
    }
}
