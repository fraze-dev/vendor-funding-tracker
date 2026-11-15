using System.Globalization;

namespace VendorFunding.Web;

/// <summary>Display formatting. Amounts are US dollars, so a fixed culture keeps them identical for every visitor.</summary>
public static class Format
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal value) => value.ToString("C2", Us);

    /// <summary>$950, $12.9K, $1.2M: for chart axes and stat tiles where precision isn't the point.</summary>
    public static string CompactMoney(decimal value)
    {
        var abs = Math.Abs(value);
        var sign = value < 0 ? "-" : "";
        return abs switch
        {
            >= 1_000_000m => $"{sign}${(abs / 1_000_000m).ToString("0.#", Us)}M",
            >= 10_000m => $"{sign}${(abs / 1_000m).ToString("0", Us)}K",
            >= 1_000m => $"{sign}${(abs / 1_000m).ToString("0.#", Us)}K",
            _ => $"{sign}${abs.ToString("0", Us)}",
        };
    }

    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy", Us);

    public static string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("MMM d, h:mm tt", Us);

    /// <summary>"2026-07" → "Jul 2026", or "Jul" when <paramref name="shortForm"/> is set.</summary>
    public static string Month(string month, bool shortForm = false) =>
        System.DateTime.TryParseExact(month, "yyyy-MM", Us, DateTimeStyles.None, out var parsed)
            ? parsed.ToString(shortForm ? "MMM" : "MMM yyyy", Us)
            : month;
}
