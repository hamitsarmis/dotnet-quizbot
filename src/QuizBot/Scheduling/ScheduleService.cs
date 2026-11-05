using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuizBot.Options;
using QuizBot.Persistence;

namespace QuizBot.Scheduling;

/// <summary>Persists and reads the per-day schedule. Scoped: it shares the caller's <see cref="QuizDbContext"/>.</summary>
public sealed partial class ScheduleService(
    QuizDbContext db,
    ScheduleGenerator generator,
    QuizClock clock,
    IOptions<QuizOptions> options,
    ILogger<ScheduleService> logger)
{
    /// <summary>
    /// Returns the current local day's slots, generating and saving them the first time the day is seen.
    /// Called at startup and on every scheduler tick, so the first tick after local midnight creates the new day.
    /// Only those two callers may generate: they never run concurrently (the initializer finishes before the
    /// scheduler starts), so two callers can never both see an empty day and plan it twice.
    /// </summary>
    public async Task<IReadOnlyList<ScheduledSlot>> EnsureTodayAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = clock.LocalDate(now);

        var slots = await GetDayAsync(today, cancellationToken);
        if (slots.Count > 0)
        {
            return slots;
        }

        slots = generator.Generate(today, options.Value.ToScheduleWindow(), clock.TimeZone, now)
            .Select(p => new ScheduledSlot { LocalDate = today, FireAtUtc = p.FireAtUtc, Status = p.Status })
            .ToList();
        db.ScheduledSlots.AddRange(slots);
        await db.SaveChangesAsync(cancellationToken);

        Log.ScheduleGenerated(logger, today, Describe(slots));
        return slots;
    }

    /// <summary>Read-only: the current local day's slots, empty if the scheduler has not planned the day yet.</summary>
    public Task<List<ScheduledSlot>> GetTodayAsync(CancellationToken cancellationToken) =>
        GetDayAsync(clock.Today, cancellationToken);

    private Task<List<ScheduledSlot>> GetDayAsync(DateOnly localDate, CancellationToken cancellationToken) =>
        db.ScheduledSlots
            .Where(s => s.LocalDate == localDate)
            .OrderBy(s => s.FireAtUtc)
            .ToListAsync(cancellationToken);

    /// <summary>Formats slots as local times, marking the ones that will not fire, e.g. <c>09:42 (skipped), 13:05</c>.</summary>
    public string Describe(IEnumerable<ScheduledSlot> slots) =>
        string.Join(", ", slots.Select(s => s.Status == SlotStatus.Pending
            ? clock.FormatLocalTime(s.FireAtUtc)
            : $"{clock.FormatLocalTime(s.FireAtUtc)} ({s.Status.ToString().ToLowerInvariant()})"));

    private static partial class Log
    {
        [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Generated schedule for {LocalDate:yyyy-MM-dd}: {Slots}")]
        public static partial void ScheduleGenerated(ILogger logger, DateOnly localDate, string slots);
    }
}
