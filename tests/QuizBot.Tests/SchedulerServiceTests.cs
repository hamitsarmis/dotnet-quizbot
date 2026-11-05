using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Scheduling;

namespace QuizBot.Tests;

/// <summary>
/// Drives the real <see cref="SchedulerService"/> loop: PeriodicTimer on a <c>FakeTimeProvider</c>, real SQLite,
/// fake messenger. Each "tick" advances fake time by 30 s and waits for the scheduler's heartbeat.
/// </summary>
public sealed class SchedulerServiceTests : IAsyncLifetime
{
    // 10:00:00 in Istanbul.
    private static readonly DateTimeOffset Start = TestHost.Local(2026, 10, 23, 10, 0);
    private static readonly DateTimeOffset SlotTime = Start.AddSeconds(20);

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = new TestHost(Start);
        await _host.InitializeAsync(TestHost.Question("gc-1"), TestHost.Question("gc-2"));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.Scheduler.StopAsync(CancellationToken.None);
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task AdvancingPastASlotSendsExactlyOneMessage()
    {
        await _host.AddSlotsAsync(SlotTime, Start.AddHours(5));
        await StartSchedulerAsync();
        Assert.Empty(_host.Messenger.Sent);

        await TickAsync();
        await TickAsync();
        await TickAsync();

        var (messageId, message) = Assert.Single(_host.Messenger.Sent);
        Assert.Contains("What about gc-", message.Html);

        var slot = await FirstSlotAsync();
        Assert.Equal(SlotStatus.Sent, slot.Status);
        Assert.Equal(Start.AddSeconds(30), slot.SentAtUtc);
        Assert.Equal(1, slot.Attempts);

        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        Assert.Equal(slot.AskedQuestionId, asked.Id);
        Assert.Equal(QuestionSource.Scheduled, asked.Source);
        Assert.Equal(messageId, asked.TelegramMessageId);
    }

    [Fact]
    public async Task FailedSendIncrementsAttemptsAndRetriesNextTick()
    {
        await _host.AddSlotsAsync(SlotTime);
        _host.Messenger.FailNextSends(1);
        await StartSchedulerAsync();

        await TickAsync();

        var afterFailure = await FirstSlotAsync();
        Assert.Equal(SlotStatus.Pending, afterFailure.Status);
        Assert.Equal(1, afterFailure.Attempts);
        Assert.Null(afterFailure.SentAtUtc);
        Assert.Empty(_host.Messenger.Sent);
        Assert.Empty(await _host.WithDbAsync(db => db.AskedQuestions.ToListAsync()));

        await TickAsync();

        var afterRetry = await FirstSlotAsync();
        Assert.Equal(SlotStatus.Sent, afterRetry.Status);
        Assert.Equal(2, afterRetry.Attempts);
        Assert.Single(_host.Messenger.Sent);
    }

    [Fact]
    public async Task ThreeFailedAttemptsMarkTheSlotFailedAndLogAnError()
    {
        await _host.AddSlotsAsync(SlotTime);
        _host.Messenger.FailNextSends(10);
        await StartSchedulerAsync();

        await TickAsync();
        await TickAsync();
        await TickAsync();
        await TickAsync();

        var slot = await FirstSlotAsync();
        Assert.Equal(SlotStatus.Failed, slot.Status);
        Assert.Equal(SchedulerService.MaxAttempts, slot.Attempts);
        Assert.Single(_host.Logs.Entries, e => e.Level == LogLevel.Error && e.EventId.Id == 102);
    }

    [Fact]
    public async Task AWriteThatFailsAfterASuccessfulSendCannotCauseEndlessResends()
    {
        await _host.AddSlotsAsync(SlotTime);
        // Simulates a database that accepts the attempt counter but fails the post-send write (disk full,
        // lock timeout): every insert into AskedQuestions aborts, which rolls back the whole SaveChanges.
        await _host.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_asks BEFORE INSERT ON AskedQuestions BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;"));

        for (var i = 0; i < 6; i++)
        {
            _host.Time.Advance(SchedulerService.TickInterval);
            try
            {
                await _host.Scheduler.TickAsync(TestContext.Current.CancellationToken);
            }
            catch (DbUpdateException)
            {
                // Expected while the post-send write keeps failing; the loop logs it and carries on.
            }
        }

        Assert.Equal(SchedulerService.MaxAttempts, _host.Messenger.Sent.Count);
        var slot = await FirstSlotAsync();
        Assert.Equal(SlotStatus.Failed, slot.Status);
        Assert.Equal(SchedulerService.MaxAttempts, slot.Attempts);
        Assert.Contains(_host.Logs.Entries, e => e.EventId.Id == 107 && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task NothingIsSentWhilePaused()
    {
        await _host.AddSlotsAsync(SlotTime, Start.AddMinutes(1));
        await _host.SetPausedAsync(true);
        await StartSchedulerAsync();

        await TickAsync();
        await TickAsync();
        await TickAsync();

        Assert.Empty(_host.Messenger.Sent);
        var slots = await _host.WithDbAsync(db => db.ScheduledSlots.ToListAsync());
        Assert.All(slots, s => Assert.Equal(SlotStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task ResumingSendsTheNextSlotButNotTheSkippedOnes()
    {
        await _host.AddSlotsAsync(SlotTime, Start.AddMinutes(2));
        await _host.SetPausedAsync(true);
        await StartSchedulerAsync();
        await TickAsync();

        await _host.SetPausedAsync(false);
        for (var i = 0; i < 4; i++)
        {
            await TickAsync();
        }

        Assert.Single(_host.Messenger.Sent);
        var statuses = await _host.WithDbAsync(db => db.ScheduledSlots.OrderBy(s => s.FireAtUtc).Select(s => s.Status).ToListAsync());
        Assert.Equal([SlotStatus.Skipped, SlotStatus.Sent], statuses);
    }

    [Fact]
    public async Task SlotsMissedWhileTheBotWasDownAreSkippedNotSentAsABurst()
    {
        await _host.AddSlotsAsync(Start.AddHours(-3), Start.AddHours(-2), Start.AddMinutes(-10));

        await StartSchedulerAsync();

        Assert.Empty(_host.Messenger.Sent);
        var slots = await _host.WithDbAsync(db => db.ScheduledSlots.ToListAsync());
        Assert.All(slots, s => Assert.Equal(SlotStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task FirstTickWithoutAScheduleGeneratesTodayWithoutABacklog()
    {
        // 16:00 local: roughly half of the 09:00-22:00 window is gone.
        _host.Time.SetUtcNow(TestHost.Local(2026, 10, 23, 16, 0));

        await StartSchedulerAsync();

        var slots = await _host.WithDbAsync(db => db.ScheduledSlots.ToListAsync());
        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.Equal(new DateOnly(2026, 10, 23), s.LocalDate));
        Assert.All(slots, s => Assert.Equal(s.FireAtUtc <= _host.Time.GetUtcNow() ? SlotStatus.Skipped : SlotStatus.Pending, s.Status));
        Assert.Empty(_host.Messenger.Sent);
    }

    [Fact]
    public async Task FirstTickAfterLocalMidnightPlansTheNewDay()
    {
        _host.Time.SetUtcNow(TestHost.Local(2026, 10, 23, 23, 59, 45));
        await _host.AddSlotsAsync(TestHost.Local(2026, 10, 23, 21, 0));
        await _host.WithDbAsync(async db =>
        {
            await db.ScheduledSlots.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SlotStatus.Sent));
        });
        await StartSchedulerAsync();

        await TickAsync();

        var newDay = await _host.WithDbAsync(db => db.ScheduledSlots.Where(s => s.LocalDate == new DateOnly(2026, 10, 24)).ToListAsync());
        Assert.Equal(4, newDay.Count);
        Assert.All(newDay, s => Assert.Equal(SlotStatus.Pending, s.Status));
        Assert.Empty(_host.Messenger.Sent);
    }

    [Fact]
    public async Task StopAsyncEndsTheLoopPromptly()
    {
        await StartSchedulerAsync();

        var stop = _host.Scheduler.StopAsync(CancellationToken.None);

        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        Assert.True(_host.Scheduler.ExecuteTask!.IsCompletedSuccessfully);
    }

    private async Task StartSchedulerAsync()
    {
        await _host.Scheduler.StartAsync(TestContext.Current.CancellationToken);
        await _host.Heartbeat.WaitForBeatAsync();
    }

    private async Task TickAsync()
    {
        _host.Time.Advance(SchedulerService.TickInterval);
        await _host.Heartbeat.WaitForBeatAsync();
    }

    private Task<ScheduledSlot> FirstSlotAsync() =>
        _host.WithDbAsync(db => db.ScheduledSlots.OrderBy(s => s.FireAtUtc).FirstAsync());
}
