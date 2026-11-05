using QuizBot.Persistence;
using QuizBot.Questions;
using QuizBot.Scheduling;

namespace QuizBot.Telegram.Commands;

public sealed class StartCommand(
    ScheduleService scheduleService,
    BotStateStore state,
    QuizClock clock,
    IQuizMessenger messenger) : IBotCommand
{
    public string Name => "start";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        // Read-only on purpose: generating here could race the scheduler's first tick after midnight.
        var remaining = (await scheduleService.GetTodayAsync(cancellationToken))
            .Where(s => s.Status == SlotStatus.Pending && s.FireAtUtc > now)
            .Select(s => clock.FormatLocalTime(s.FireAtUtc))
            .ToList();

        var schedule = remaining.Count > 0
            ? $"Still to come today: {string.Join(", ", remaining)} ({QuestionFormatter.Escape(clock.TimeZone.Id)})."
            : "No more scheduled questions today.";
        var paused = await state.IsPausedAsync(cancellationToken)
            ? "\n⏸ Scheduled questions are paused; /resume restarts them."
            : "";

        await messenger.SendAsync(
            new OutgoingMessage($"👋 <b>QuizBot is running.</b>\n{schedule}{paused}\n\n{QuestionFormatter.Escape(BotTexts.FullHelp)}"),
            cancellationToken);
    }
}
