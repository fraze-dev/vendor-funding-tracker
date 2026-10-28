namespace VendorFunding.Domain;

/// <summary>An inclusive range of calendar dates.</summary>
public readonly record struct DateRange
{
    public DateRange(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new ArgumentException($"End date {end} is before start date {start}.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public DateOnly Start { get; }
    public DateOnly End { get; }

    public int Days => End.DayNumber - Start.DayNumber + 1;

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>Returns the overlapping dates, or null when the ranges don't overlap.</summary>
    public DateRange? Intersect(DateRange other)
    {
        var start = Start > other.Start ? Start : other.Start;
        var end = End < other.End ? End : other.End;
        return start <= end ? new DateRange(start, end) : null;
    }
}
