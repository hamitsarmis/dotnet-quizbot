namespace QuizBot.Scheduling;

/// <summary>The daily local-time window in which scheduled questions may fire.</summary>
public sealed record ScheduleWindow(TimeOnly Start, TimeOnly End, int SlotsPerDay, TimeSpan MinGap)
{
    public TimeSpan Length => End - Start;

    /// <summary>Returns a human-readable problem with the window, or <c>null</c> when it can be scheduled.</summary>
    public string? Validate()
    {
        if (End <= Start)
        {
            return $"Window end ({End:HH\\:mm}) must be after window start ({Start:HH\\:mm}); windows across midnight are not supported.";
        }

        if (SlotsPerDay < 1)
        {
            return $"Slots per day must be at least 1, got {SlotsPerDay}.";
        }

        if (MinGap < TimeSpan.Zero)
        {
            return $"Minimum gap must not be negative, got {MinGap}.";
        }

        if (MinGap * (SlotsPerDay - 1) > Length)
        {
            return $"{SlotsPerDay} slots with a {MinGap.TotalMinutes:0}-minute gap do not fit in a {Length.TotalMinutes:0}-minute window.";
        }

        return null;
    }
}
