using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuizBot.Persistence;
using QuizBot.Questions;
using QuizBot.Scheduling;
using QuizBot.Telegram;
using QuizBot.Telegram.Commands;

namespace QuizBot.Tests;

/// <summary>
/// Commands end to end through <see cref="UpdateHandlerService.HandleUpdateAsync"/>: chat filter, per-update scope,
/// dispatcher, command, real SQLite, fake messenger.
/// </summary>
public sealed class CommandHandlingTests : IAsyncLifetime
{
    private const long StrangerChatId = 999;
    private static readonly DateTimeOffset Now = TestHost.Local(2026, 10, 23, 10, 0);

    private TestHost _host = null!;
    private long _nextUpdateId = 1;

    public async ValueTask InitializeAsync()
    {
        _host = new TestHost(Now);
        await _host.InitializeAsync(
            TestHost.Question("gc-1", topic: "gc"),
            TestHost.Question("gc-2", topic: "gc"),
            TestHost.Question("async-1", topic: "async"));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task MessagesFromAnotherChatGetNoReplyAndOneWarning()
    {
        await Handle(new IncomingMessage(_nextUpdateId++, StrangerChatId, "/start"));
        await Handle(new IncomingMessage(_nextUpdateId++, StrangerChatId, "/ask"));
        await Handle(new IncomingCallback(_nextUpdateId++, StrangerChatId, "cb-1", 5, "g:1:k"));

        Assert.Empty(_host.Messenger.Sent);
        Assert.Empty(_host.Messenger.CallbackAnswers);
        Assert.Empty(await _host.WithDbAsync(db => db.AskedQuestions.ToListAsync()));
        var warning = Assert.Single(_host.Logs.Entries, e => e.EventId.Id == 400);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(StrangerChatId.ToString(), warning.Message);
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("/nonsense")]
    [InlineData("/")]
    public async Task UnknownCommandsAndFreeTextGetTheOneLineHelp(string text)
    {
        await Send(text);

        Assert.Equal(BotTexts.OneLineHelp, LastReply());
    }

    [Fact]
    public async Task FreeTextIsNeverLogged()
    {
        await Send("my secret diary entry");

        Assert.DoesNotContain(_host.Logs.Entries, e => e.Message.Contains("secret diary"));
    }

    [Theory]
    [InlineData("/ask", "ask", "")]
    [InlineData("  /ASK   gc  ", "ask", "gc")]
    [InlineData("/ask@MyQuizBot gc", "ask", "gc")]
    [InlineData("/answer\nextra", "answer", "extra")]
    [InlineData("just text", null, "")]
    public void ParseSplitsCommandAndArguments(string text, string? name, string arguments)
    {
        Assert.Equal((name, arguments), CommandDispatcher.Parse(text));
    }

    [Fact]
    public async Task StartConfirmsAndListsTodaysRemainingSlots()
    {
        await _host.AddSlotsAsync(TestHost.Local(2026, 10, 23, 9, 30), TestHost.Local(2026, 10, 23, 13, 5), TestHost.Local(2026, 10, 23, 20, 45));

        await Send("/start");

        var reply = LastReply();
        Assert.Contains("QuizBot is running", reply);
        Assert.Contains("13:05, 20:45 (Europe/Istanbul)", reply);
        Assert.DoesNotContain("09:30", reply);
        Assert.Contains("/ask [topic]", reply);
    }

    [Fact]
    public async Task AskSendsAQuestionWithoutConsumingAScheduledSlot()
    {
        await _host.AddSlotsAsync(Now.AddHours(1));

        await Send("/ask");

        Assert.Contains("What about", LastReply());
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        Assert.Equal(QuestionSource.Manual, asked.Source);
        Assert.Equal(_host.Messenger.Sent[^1].MessageId, asked.TelegramMessageId);
        var slot = await _host.WithDbAsync(db => db.ScheduledSlots.SingleAsync());
        Assert.Equal(SlotStatus.Pending, slot.Status);
    }

    [Fact]
    public async Task AskWithATopicIsExactAndCaseInsensitive()
    {
        for (var i = 0; i < 5; i++)
        {
            await Send("/ask ASYNC");
        }

        var askedIds = await _host.WithDbAsync(db => db.AskedQuestions.Select(a => a.QuestionId).Distinct().ToListAsync());
        Assert.Equal(["async-1"], askedIds);
    }

    [Fact]
    public async Task AskWithAnUnknownTopicListsTheTopics()
    {
        await Send("/ask kubernetes");

        Assert.Equal("Unknown topic <b>kubernetes</b>. Topics: async, gc.", LastReply());
        Assert.Empty(await _host.WithDbAsync(db => db.AskedQuestions.ToListAsync()));
    }

    [Fact]
    public async Task AnswerWithNoOpenQuestionReturnsTheHelpText()
    {
        await Send("/answer");

        var reply = LastReply();
        Assert.Contains(BotTexts.OneLineHelp, reply);
        Assert.Null(_host.Messenger.Sent[^1].Message.Buttons);
    }

    [Fact]
    public async Task AnswerRevealsTheLatestUnrevealedQuestionAsAReplyWithGradeButtons()
    {
        await Send("/ask gc");
        var (questionMessageId, _) = _host.Messenger.Sent[^1];
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());

        _host.Time.Advance(TimeSpan.FromMinutes(3));
        await Send("/answer");

        var (_, reveal) = _host.Messenger.Sent[^1];
        Assert.StartsWith("💡 <b>Model answer</b>", reveal.Html);
        Assert.Contains("A follow-up?", reveal.Html);
        Assert.Equal(questionMessageId, reveal.ReplyToMessageId);
        Assert.Equal(
            [$"g:{asked.Id}:k", $"g:{asked.Id}:p", $"g:{asked.Id}:d"],
            reveal.Buttons!.Select(b => b.CallbackData));
        var revealed = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        Assert.Equal(Now.AddMinutes(3), revealed.RevealedAtUtc);
    }

    [Fact]
    public async Task AnswerPrefersAnOlderUnrevealedQuestionOverRevealingAgain()
    {
        await Send("/ask gc");
        _host.Time.Advance(TimeSpan.FromMinutes(1));
        await Send("/ask async");
        await Send("/answer");

        await Send("/answer");

        var revealedIds = await _host.WithDbAsync(db =>
            db.AskedQuestions.Where(a => a.RevealedAtUtc != null).Select(a => a.QuestionId).ToListAsync());
        Assert.Equal(2, revealedIds.Count);
        Assert.Contains("async-1", revealedIds);
    }

    [Fact]
    public async Task AnswerWhenEverythingIsRevealedReRevealsTheLatestWithItsGrade()
    {
        await Send("/ask gc");
        await Send("/answer");
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        await Press(GradeCallbackHandler.CallbackData(asked.Id, Grade.Partially));

        await Send("/answer");

        var (_, reveal) = _host.Messenger.Sent[^1];
        Assert.Contains("Model answer", reveal.Html);
        Assert.Contains("<b>Your grade:</b> 🟡 Partially", reveal.Html);
        Assert.Null(reveal.Buttons);
    }

    [Fact]
    public async Task GradingPersistsTheGradeAndEditsTheMessageWithoutTheKeyboard()
    {
        await Send("/ask gc");
        await Send("/answer");
        var (revealMessageId, _) = _host.Messenger.Sent[^1];
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        _host.Time.Advance(TimeSpan.FromMinutes(1));

        await Press(GradeCallbackHandler.CallbackData(asked.Id, Grade.DidntKnow), revealMessageId, callbackId: "cb-42");

        var graded = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        Assert.Equal(Grade.DidntKnow, graded.Grade);
        Assert.Equal(Now.AddMinutes(1), graded.GradedAtUtc);
        Assert.Equal(("cb-42", "Saved: ❌ Didn't know"), _host.Messenger.CallbackAnswers[^1]);
        var (editedMessageId, html) = Assert.Single(_host.Messenger.Edits);
        Assert.Equal(revealMessageId, editedMessageId);
        Assert.EndsWith("<b>Your grade:</b> ❌ Didn't know", html);
    }

    [Fact]
    public async Task AGradeCallbackForAnAlreadyGradedQuestionIsIgnored()
    {
        await Send("/ask gc");
        await Send("/answer");
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        await Press(GradeCallbackHandler.CallbackData(asked.Id, Grade.KnewIt), callbackId: "first");
        var gradedAt = (await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync())).GradedAtUtc;
        _host.Time.Advance(TimeSpan.FromMinutes(5));

        await Press(GradeCallbackHandler.CallbackData(asked.Id, Grade.DidntKnow), callbackId: "second");

        var stored = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        Assert.Equal(Grade.KnewIt, stored.Grade);
        Assert.Equal(gradedAt, stored.GradedAtUtc);
        Assert.Single(_host.Messenger.Edits);
        Assert.Equal(("second", "Already graded: ✅ Knew it"), _host.Messenger.CallbackAnswers[^1]);
    }

    [Fact]
    public async Task GradingStillEditsTheMessageWhenTheAcknowledgementFails()
    {
        await Send("/ask gc");
        await Send("/answer");
        var (revealMessageId, _) = _host.Messenger.Sent[^1];
        var sentBefore = _host.Messenger.Sent.Count;
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        // Telegram rejects acks for queries older than a few minutes, e.g. a tap made while the bot was down.
        _host.Messenger.FailNextCallbackAnswers(1);

        await Press(GradeCallbackHandler.CallbackData(asked.Id, Grade.KnewIt), revealMessageId);

        Assert.Equal(Grade.KnewIt, (await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync())).Grade);
        Assert.Equal(revealMessageId, Assert.Single(_host.Messenger.Edits).MessageId);
        Assert.Equal(sentBefore, _host.Messenger.Sent.Count);
        Assert.DoesNotContain(_host.Logs.Entries, e => e.EventId.Id == 402);
        Assert.Contains(_host.Logs.Entries, e => e.EventId.Id == 313);
    }

    [Fact]
    public async Task LongRevealsKeepEveryPartWhenGradedAndShownAgain()
    {
        var answer = string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"Paragraph {i}: " + new string('x', 320)));
        string[] followups = [.. Enumerable.Range(0, 12).Select(i => $"Follow-up {i} " + new string('y', 80))];
        await _host.InitializeAsync(new QuestionDefinition("long-1", "long", "deep", "A long one?", answer, followups, []));
        await Send("/ask long");
        await Send("/answer");
        var (revealMessageId, _) = _host.Messenger.Sent[^1];
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());

        await Press(GradeCallbackHandler.CallbackData(asked.Id, Grade.KnewIt), revealMessageId);
        await Send("/answer");

        // The edit replaces only the last part (the one with the keyboard) and still fits one message.
        var (_, edited) = Assert.Single(_host.Messenger.Edits);
        Assert.InRange(edited.Length, 1, 4096);
        Assert.DoesNotContain("Paragraph 0:", edited);
        Assert.EndsWith(GradeCallbackHandler.GradeLine(Grade.KnewIt), edited);

        // Showing it again sends the whole reveal; the messenger splits it.
        var reRevealed = _host.Messenger.Sent[^1].Message.Html;
        Assert.True(reRevealed.Length > MessageSplitter.MaxLength);
        Assert.Contains("Paragraph 0:", reRevealed);
        Assert.EndsWith(GradeCallbackHandler.GradeLine(Grade.KnewIt), reRevealed);
    }

    [Theory]
    [InlineData("g:9999:k")]
    [InlineData("g:abc:k")]
    [InlineData("g:1:x")]
    [InlineData("something-else")]
    public async Task StaleOrMalformedCallbacksAreAcknowledgedAndChangeNothing(string data)
    {
        await Send("/ask gc");

        await Press(data, callbackId: "cb");

        Assert.Equal("cb", _host.Messenger.CallbackAnswers[^1].CallbackId);
        Assert.Empty(_host.Messenger.Edits);
        Assert.Null((await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync())).Grade);
    }

    [Fact]
    public async Task GradesFeedTheSelectionWeights()
    {
        await Send("/ask async");
        await Send("/answer");
        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        await Press(GradeCallbackHandler.CallbackData(asked.Id, Grade.DidntKnow));

        IReadOnlyList<QuestionCandidate> candidates = [];
        await _host.WithScopeAsync(async sp => candidates = await sp.GetRequiredService<QuestionAsker>().LoadCandidatesAsync(CancellationToken.None));

        var graded = Assert.Single(candidates, c => c.Id == "async-1");
        Assert.Equal([QuestionOutcome.DidntKnow], graded.OutcomesNewestFirst);
        Assert.Equal(2.5, QuestionSelector.Weight(graded.OutcomesNewestFirst));
        Assert.All(candidates.Where(c => c.Id != "async-1"), c => Assert.Equal(3.0, QuestionSelector.Weight(c.OutcomesNewestFirst)));
    }

    [Fact]
    public async Task SkipMarksTheLatestQuestionSkipped()
    {
        await Send("/ask gc");

        await Send("/skip");

        var asked = await _host.WithDbAsync(db => db.AskedQuestions.SingleAsync());
        Assert.Equal(Now, asked.SkippedAtUtc);
        Assert.Contains("Skipped", LastReply());

        await Send("/skip");
        Assert.Contains("Nothing to skip", LastReply());
    }

    [Fact]
    public async Task PauseAndResumeToggleScheduledSendsOnly()
    {
        await Send("/pause");
        Assert.True(await IsPausedAsync());
        Assert.Contains("paused", LastReply());

        await Send("/ask");
        Assert.Contains("What about", LastReply());

        await Send("/start");
        Assert.Contains("paused", LastReply());

        await Send("/resume");
        Assert.False(await IsPausedAsync());
        Assert.Contains("resumed", LastReply());
    }

    [Fact]
    public async Task TopicsShowCountsAndPassRates()
    {
        await GradeAsync("gc", Grade.KnewIt);
        await GradeAsync("gc", Grade.DidntKnow);

        await Send("/topics");

        var reply = LastReply();
        Assert.Contains("• <b>async</b>: 1 questions · not graded yet", reply);
        Assert.Contains("• <b>gc</b>: 2 questions · 50% of 2 graded", reply);
    }

    [Fact]
    public async Task StatsShowTotalsTheLastSevenDaysAndTheStreak()
    {
        await GradeAsync("gc", Grade.KnewIt);
        _host.Time.Advance(TimeSpan.FromDays(1));
        await GradeAsync("async", Grade.Partially);
        _host.Time.Advance(TimeSpan.FromDays(8));
        await GradeAsync("gc", Grade.DidntKnow);
        await Send("/ask");
        await Send("/skip");

        await Send("/stats");

        var reply = LastReply();
        Assert.Contains("All time: 4 asked · 3 revealed · ✅ 1 · 🟡 1 · ❌ 1 · ⏭ 1 skipped", reply);
        Assert.Contains("Last 7 days: 2 asked · 1 revealed · ✅ 0 · 🟡 0 · ❌ 1 · ⏭ 1 skipped", reply);
        Assert.Contains("Streak: 1 day ", reply);
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2 }, 3)]
    [InlineData(new[] { 1, 2 }, 2)]
    [InlineData(new[] { 0, 2, 3 }, 1)]
    [InlineData(new[] { 2, 3 }, 0)]
    [InlineData(new int[0], 0)]
    public void StreakCountsConsecutiveDaysEndingTodayOrYesterday(int[] daysAgo, int expected)
    {
        var today = new DateOnly(2026, 10, 23);

        Assert.Equal(expected, StatsCommand.CurrentStreak(daysAgo.Select(d => today.AddDays(-d)), today));
    }

    [Fact]
    public async Task TheBotTokenNeverAppearsInLogs()
    {
        await Send("/start");
        await Send("/ask");
        await Send("/answer");
        await Send("/stats");
        _host.Messenger.FailNextSends(5);
        await Send("/topics");

        Assert.DoesNotContain(_host.Logs.Entries, e => (e.Message + e.Exception).Contains(TestHost.BotToken));
    }

    [Fact]
    public async Task PollingConfirmsUpdatesOnlyAfterHandlingThem()
    {
        _host.Messenger.EnqueueUpdates(
            new IncomingMessage(7, TestHost.AllowedChatId, "hi"),
            new IncomingMessage(8, StrangerChatId, "hi"));

        await _host.Updates.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => _host.Messenger.PolledOffsets.Contains(9), "the next poll confirms update 8");
        await _host.Updates.StopAsync(CancellationToken.None);

        Assert.Equal([0, 9], _host.Messenger.PolledOffsets);
        Assert.Single(_host.Messenger.Sent);
    }

    [Fact]
    public async Task AFailingUpdateIsLoggedAndDoesNotBlockTheNextOne()
    {
        // Both the help reply and the "something went wrong" report fail.
        _host.Messenger.FailNextSends(2);
        _host.Messenger.EnqueueUpdates(new IncomingMessage(3, TestHost.AllowedChatId, "hi"));

        await _host.Updates.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => _host.Messenger.PolledOffsets.Contains(4), "the poison update is confirmed");
        _host.Messenger.EnqueueUpdates(new IncomingMessage(4, TestHost.AllowedChatId, "hi again"));
        await Eventually.WaitUntilAsync(() => _host.Messenger.PolledOffsets.Contains(5), "the next update is handled");
        await _host.Updates.StopAsync(CancellationToken.None);

        Assert.Contains(_host.Logs.Entries, e => e.EventId.Id == 402 && e.Level == LogLevel.Error);
        Assert.Single(_host.Messenger.Sent);
    }

    private async Task GradeAsync(string topic, Grade grade)
    {
        await Send($"/ask {topic}");
        await Send("/answer");
        var latest = await _host.WithDbAsync(db => db.AskedQuestions.OrderByDescending(a => a.Id).FirstAsync());
        await Press(GradeCallbackHandler.CallbackData(latest.Id, grade));
    }

    private Task<bool> IsPausedAsync() =>
        _host.WithDbAsync(db => new BotStateStore(db).IsPausedAsync(CancellationToken.None));

    private string LastReply() => _host.Messenger.Sent[^1].Message.Html;

    private Task Send(string text) => Handle(new IncomingMessage(_nextUpdateId++, TestHost.AllowedChatId, text));

    private Task Press(string data, long messageId = 1, string callbackId = "cb") =>
        Handle(new IncomingCallback(_nextUpdateId++, TestHost.AllowedChatId, callbackId, messageId, data));

    private Task Handle(IncomingUpdate update) => _host.Updates.HandleUpdateAsync(update, TestContext.Current.CancellationToken);
}
