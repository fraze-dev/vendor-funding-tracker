using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VendorFunding.Domain;

/// <summary>
/// A reporting period such as "2026", "2026-Q3" or "2026-09".
/// Uses calendar quarters and months; a 52/53-week retail calendar is a planned enhancement.
/// </summary>
public sealed partial record FiscalPeriod(string Label, DateRange Range)
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out FiscalPeriod? period)
    {
        period = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().ToUpperInvariant();

        if (QuarterPattern().Match(text) is { Success: true } q)
        {
            var year = int.Parse(q.Groups["year"].Value, CultureInfo.InvariantCulture);
            var quarter = int.Parse(q.Groups["quarter"].Value, CultureInfo.InvariantCulture);
            var start = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
            period = new FiscalPeriod(text, new DateRange(start, start.AddMonths(3).AddDays(-1)));
            return true;
        }

        if (MonthPattern().Match(text) is { Success: true } m)
        {
            var year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(m.Groups["month"].Value, CultureInfo.InvariantCulture);
            if (month is < 1 or > 12)
            {
                return false;
            }

            var start = new DateOnly(year, month, 1);
            period = new FiscalPeriod(text, new DateRange(start, start.AddMonths(1).AddDays(-1)));
            return true;
        }

        if (YearPattern().Match(text) is { Success: true } y)
        {
            var year = int.Parse(y.Groups["year"].Value, CultureInfo.InvariantCulture);
            period = new FiscalPeriod(text, new DateRange(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31)));
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(?<year>\d{4})-Q(?<quarter>[1-4])$")]
    private static partial Regex QuarterPattern();

    [GeneratedRegex(@"^(?<year>\d{4})-(?<month>\d{2})$")]
    private static partial Regex MonthPattern();

    [GeneratedRegex(@"^(?<year>\d{4})$")]
    private static partial Regex YearPattern();
}
