using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Questions;

namespace QuizBot.Scheduling;

/// <summary>
/// The ticker: every 30 seconds it makes sure today's schedule exists and sends every pending slot that is due.
/// </summary>
/// <remarks>
/// Captive dependency lesson: hosted services are singletons, and <see cref="QuizDbContext"/> is scoped.
/// Injecting the context here would capture one instance for the life of the process: an ever-growing change
/// tracker, stale entities, and a context shared between ticks. <c>ValidateScopes</c> rejects that graph at
/// startup. Instead, every tick opens its own scope via <see cref="IServiceScopeFactory"/>, so each tick is
/// one fresh unit of work, disposed when the tick ends.
/// </remarks>
public sealed partial class SchedulerService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IHeartbeat heartbeat,
    ILogger<SchedulerService> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    public const int MaxAttempts = 3;

    /// <summary>
    /// A pending slot that is overdue by more than this was missed while the bot was down. It is skipped rather
    /// than sent late, so a restart never delivers a burst of old questions. Normally far longer than the
    /// <see cref="MaxAttempts"/> retries, which run one tick apart.
    /// </summary>
    public static readonly TimeSpan MissedSlotGrace = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer takes the TimeProvider, so tests advance a FakeTimeProvider instead of waiting 30 s.
        // Unlike System.Threading.Timer it never overlaps ticks: a slow tick delays the next one.
        using var timer = new PeriodicTimer(TickInterval, timeProvider);
        try
        {
            do
            {
                await TickSafelyAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: the host cancelled the stopping token.
        }
    }

    private async Task TickSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TickAsync(stoppingToken);
            heartbeat.Beat();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // An exception escaping ExecuteAsync stops the whole host (BackgroundServiceExceptionBehavior.StopHost).
            // One bad tick (database locked, disk full) should not kill the bot, and the missing heartbeat
            // already reports it to Docker if it keeps happening.
            Log.TickFailed(logger, ex);
        }
    }

    internal async Task TickAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<ScheduleService>().EnsureTodayAsync(stoppingToken);

        var db = services.GetRequiredService<QuizDbContext>();
        var now = timeProvider.GetUtcNow();
        var due = await db.ScheduledSlots
            .Where(s => s.Status == SlotStatus.Pending && s.FireAtUtc <= now)
            .OrderBy(s => s.FireAtUtc)
            .ToListAsync(stoppingToken);
        if (due.Count == 0)
        {
            return;
        }

        var paused = await services.GetRequiredService<BotStateStore>().IsPausedAsync(stoppingToken);
        var asker = services.GetRequiredService<QuestionAsker>();

        foreach (var slot in due)
        {
            if (paused)
            {
                slot.Status = SlotStatus.Skipped;
                Log.SlotSkippedWhilePaused(logger, slot.Id);
            }
            else if (now - slot.FireAtUtc > MissedSlotGrace)
            {
                slot.Status = SlotStatus.Skipped;
                Log.SlotMissed(logger, slot.Id, slot.FireAtUtc);
            }
            else if (slot.Attempts >= MaxAttempts)
            {
                // Earlier attempts reached Telegram but could not be recorded (see below). Stop here rather than
                // sending the same slot again.
                slot.Status = SlotStatus.Failed;
                Log.SlotAttemptsExhausted(logger, slot.Id, slot.Attempts);
            }
            else
            {
                // Record the attempt before the side effect. If this write fails, nothing is sent; if the write
                // after the send fails, the persisted count still caps the retries at MaxAttempts.
                slot.Attempts++;
                await db.SaveChangesAsync(stoppingToken);
                await SendAsync(slot, asker, stoppingToken);
            }

            // Delivery is at-least-once. The message goes out first and SentAtUtc is committed afterwards; if the
            // process dies in between, or this write fails, the slot is still Pending and the next tick sends it
            // again, at most MaxAttempts times in total. The alternative (mark sent, then send) is at-most-once,
            // which silently loses questions. For a quiz bot a rare duplicate is the cheaper failure.
            // CancellationToken.None: once Telegram has accepted the message, shutdown must not cancel recording it.
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task SendAsync(ScheduledSlot slot, QuestionAsker asker, CancellationToken stoppingToken)
    {
        try
        {
            var asked = await asker.AskAsync(topic: null, QuestionSource.Scheduled, stoppingToken);
            if (asked is null)
            {
                slot.Status = SlotStatus.Failed;
                Log.NoActiveQuestions(logger, slot.Id);
                return;
            }

            slot.Status = SlotStatus.Sent;
            slot.SentAtUtc = timeProvider.GetUtcNow();
            slot.AskedQuestion = asked;
            Log.SlotSent(logger, slot.Id, asked.QuestionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            if (slot.Attempts >= MaxAttempts)
            {
                slot.Status = SlotStatus.Failed;
                Log.SlotFailed(logger, ex, slot.Id, slot.Attempts);
            }
            else
            {
                Log.SlotSendRetry(logger, ex, slot.Id, slot.Attempts, MaxAttempts);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Sent scheduled slot {SlotId} with question {QuestionId}")]
        public static partial void SlotSent(ILogger logger, int slotId, string questionId);

        [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Sending slot {SlotId} failed (attempt {Attempt} of {MaxAttempts}); retrying next tick")]
        public static partial void SlotSendRetry(ILogger logger, Exception exception, int slotId, int attempt, int maxAttempts);

        [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Slot {SlotId} failed after {Attempts} attempts and will not be sent")]
        public static partial void SlotFailed(ILogger logger, Exception exception, int slotId, int attempts);

        [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Skipped slot {SlotId}: scheduled questions are paused")]
        public static partial void SlotSkippedWhilePaused(ILogger logger, int slotId);

        [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Skipped slot {SlotId} due at {FireAtUtc:u}: missed while the bot was down")]
        public static partial void SlotMissed(ILogger logger, int slotId, DateTimeOffset fireAtUtc);

        [LoggerMessage(EventId = 105, Level = LogLevel.Error, Message = "Slot {SlotId} failed: there are no active questions")]
        public static partial void NoActiveQuestions(ILogger logger, int slotId);

        [LoggerMessage(EventId = 106, Level = LogLevel.Error, Message = "Scheduler tick failed")]
        public static partial void TickFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 107, Level = LogLevel.Error, Message = "Slot {SlotId} failed: {Attempts} attempts were made but none could be recorded")]
        public static partial void SlotAttemptsExhausted(ILogger logger, int slotId, int attempts);
    }
}
