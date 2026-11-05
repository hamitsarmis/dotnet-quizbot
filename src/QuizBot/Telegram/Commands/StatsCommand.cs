using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Scheduling;

namespace QuizBot.Telegram.Commands;

/// <summary><c>/stats</c>: totals overall and for the last 7 local days, plus the current grading streak.</summary>
public sealed class StatsCommand(QuizDbContext db, QuizClock clock, IQuizMessenger messenger) : IBotCommand
{
    public string Name => "stats";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        var asks = await db.AskedQuestions
            .Select(a => new Ask(a.AskedAtUtc, a.RevealedAtUtc != null, a.Grade, a.GradedAtUtc, a.SkippedAtUtc != null))
            .ToListAsync(cancellationToken);

        var today = clock.Today;
        var weekStartUtc = clock.StartOfDayUtc(today.AddDays(-6));
        var streak = CurrentStreak(asks.Where(a => a.GradedAtUtc is not null).Select(a => clock.LocalDate(a.GradedAtUtc!.Value)), today);

        await messenger.SendAsync(
            new OutgoingMessage(
                $"""
                <b>Stats</b>
                All time: {Summary(asks)}
                Last 7 days: {Summary(asks.Where(a => a.AskedAtUtc >= weekStartUtc).ToList())}
                🔥 Streak: {streak} {(streak == 1 ? "day" : "days")} with at least one grade
                """),
            cancellationToken);
    }

    /// <summary>
    /// Consecutive local days with at least one grade, ending today, or yesterday when nothing is graded yet today
    /// (the streak is not broken until the day is over).
    /// </summary>
    internal static int CurrentStreak(IEnumerable<DateOnly> gradedDays, DateOnly today)
    {
        var days = gradedDays.ToHashSet();
        var day = days.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }

        return streak;
    }

    private static string Summary(IReadOnlyCollection<Ask> asks) =>
        $"{asks.Count} asked · {asks.Count(a => a.Revealed)} revealed · " +
        $"✅ {asks.Count(a => a.Grade == Grade.KnewIt)} · 🟡 {asks.Count(a => a.Grade == Grade.Partially)} · " +
        $"❌ {asks.Count(a => a.Grade == Grade.DidntKnow)} · ⏭ {asks.Count(a => a.Skipped && a.Grade is null)} skipped";

    private sealed record Ask(DateTimeOffset AskedAtUtc, bool Revealed, Grade? Grade, DateTimeOffset? GradedAtUtc, bool Skipped);
}
