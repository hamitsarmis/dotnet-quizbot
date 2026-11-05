using System.Globalization;
using Microsoft.Extensions.Options;
using QuizBot.Options;

namespace QuizBot.Scheduling;

/// <summary>
/// "Now" and "today" in the configured time zone. Wraps <see cref="TimeProvider"/> so tests drive it with
/// <c>FakeTimeProvider</c>; the container's own time zone (usually UTC) is never used.
/// </summary>
public sealed class QuizClock(TimeProvider timeProvider, IOptions<QuizOptions> options)
{
    // Reading options.Value runs the validators first, so an unknown id fails with QuizOptions.Validate's
    // clear message (missing tzdata?) rather than a bare TimeZoneNotFoundException from this line.
    public TimeZoneInfo TimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public DateOnly Today => LocalDate(UtcNow);

    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);

    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    public DateTimeOffset StartOfDayUtc(DateOnly localDate) =>
        ScheduleGenerator.ToUtc(localDate.ToDateTime(TimeOnly.MinValue), TimeZone);

    public string FormatLocalTime(DateTimeOffset instant) => ToLocal(instant).ToString("HH:mm", CultureInfo.InvariantCulture);
}
