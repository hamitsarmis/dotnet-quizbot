namespace QuizBot.Scheduling;

/// <summary>
/// Pure schedule generation: no clock, no I/O. "Now" and randomness are inputs, so a seeded
/// <see cref="Random"/> and a fixed instant make every schedule reproducible in tests.
/// </summary>
public sealed class ScheduleGenerator(Random random)
{
    /// <summary>
    /// Plans <see cref="ScheduleWindow.SlotsPerDay"/> fire times for <paramref name="localDate"/>.
    /// The window is split into equal buckets with one uniformly random time per bucket, then the
    /// minimum gap is enforced by nudging later slots forward. That gives spread without rejection sampling.
    /// Slots at or before <paramref name="nowUtc"/> come back <see cref="SlotStatus.Skipped"/>, so a
    /// schedule generated mid-day (for example after downtime) never produces a backlog.
    /// </summary>
    /// <exception cref="ArgumentException">The window cannot be scheduled (end before start, gaps that do not fit).</exception>
    public IReadOnlyList<PlannedSlot> Generate(DateOnly localDate, ScheduleWindow window, TimeZoneInfo timeZone, DateTimeOffset nowUtc)
    {
        if (window.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(window));
        }

        // Work on the UTC timeline: elapsed time between slots is then real time even on a DST change day.
        var startUtc = ToUtc(localDate.ToDateTime(window.Start), timeZone);
        var endUtc = ToUtc(localDate.ToDateTime(window.End), timeZone);
        var count = window.SlotsPerDay;
        var bucketTicks = (endUtc - startUtc).Ticks / count;

        var slots = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            var bucketStart = startUtc.AddTicks(bucketTicks * i);
            var offset = TimeSpan.FromSeconds(random.NextInt64(bucketTicks / TimeSpan.TicksPerSecond));
            slots[i] = bucketStart + offset;

            if (i > 0 && slots[i] - slots[i - 1] < window.MinGap)
            {
                slots[i] = slots[i - 1] + window.MinGap;
            }
        }

        // With MinGap <= bucket length the forward pass can never cross the window end. A larger gap
        // that still fits (Validate guarantees that) may overshoot, so pull the tail back inside the window.
        if (slots[^1] > endUtc)
        {
            slots[^1] = endUtc;
            for (var i = count - 2; i >= 0 && slots[i + 1] - slots[i] < window.MinGap; i--)
            {
                slots[i] = slots[i + 1] - window.MinGap;
            }
        }

        return slots
            .Select(fireAt => new PlannedSlot(fireAt, fireAt <= nowUtc ? SlotStatus.Skipped : SlotStatus.Pending))
            .ToList();
    }

    /// <summary>Converts a local wall-clock time to UTC, stepping over DST gaps and taking the first occurrence of repeated times.</summary>
    internal static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
