namespace QuantConnect.Genetic;

/// <summary>An inclusive chronological validation interval.</summary>
public sealed record WalkForwardWindow(int Index, DateOnly Start, DateOnly End)
{
    public static IReadOnlyList<WalkForwardWindow> Split(DateOnly start, DateOnly end, int windowCount)
    {
        if (start > end) throw new ArgumentException("The start date must not follow the end date.", nameof(start));
        if (windowCount < 1) throw new ArgumentOutOfRangeException(nameof(windowCount), "At least one validation window is required.");

        var totalDays = (end.DayNumber - start.DayNumber) + 1;
        if (windowCount > totalDays) throw new ArgumentOutOfRangeException(nameof(windowCount), "There cannot be more windows than calendar days in the validation range.");

        var windows = new WalkForwardWindow[windowCount];
        for (var index = 0; index < windowCount; index++)
        {
            var firstDayOffset = totalDays * index / windowCount;
            var nextDayOffset = totalDays * (index + 1) / windowCount;
            windows[index] = new WalkForwardWindow(
                index,
                start.AddDays(firstDayOffset),
                start.AddDays(nextDayOffset - 1));
        }

        return windows;
    }
}
