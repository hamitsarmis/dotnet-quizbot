using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using QuizBot.Questions;

namespace QuizBot.Tests;

public sealed class QuestionSyncTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task FirstLoadInsertsEveryQuestion()
    {
        var result = await Sync(Q("gc-1"), Q("gc-2"));

        Assert.Equal(new QuestionSyncResult(Added: 2, Updated: 0, Deactivated: 0, Active: 2), result);
        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.Questions.CountAsync(q => q.IsActive, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadingTwiceIsIdempotent()
    {
        await Sync(Q("gc-1"), Q("gc-2"));
        _time.Advance(TimeSpan.FromHours(1));

        var second = await Sync(Q("gc-1"), Q("gc-2"));

        Assert.Equal(new QuestionSyncResult(0, 0, 0, 2), second);
        await using var db = _database.CreateContext();
        Assert.All(await db.Questions.ToListAsync(TestContext.Current.CancellationToken),
            q => Assert.Equal(new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero), q.UpdatedAtUtc));
    }

    [Fact]
    public async Task EditedAnswerUpdatesTheStoredQuestion()
    {
        await Sync(Q("gc-1"));
        _time.Advance(TimeSpan.FromDays(1));

        var result = await Sync(Q("gc-1") with { Answer = "A better answer.", Followups = ["New follow-up?"] });

        Assert.Equal(1, result.Updated);
        await using var db = _database.CreateContext();
        var stored = await db.Questions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("A better answer.", stored.Answer);
        Assert.Equal(["New follow-up?"], stored.GetFollowups());
        Assert.Equal(_time.GetUtcNow(), stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task RemovedQuestionIsDeactivatedNotDeleted()
    {
        await Sync(Q("gc-1"), Q("gc-2"));

        var result = await Sync(Q("gc-1"));

        Assert.Equal(1, result.Deactivated);
        await using var db = _database.CreateContext();
        var removed = await db.Questions.SingleAsync(q => q.Id == "gc-2", TestContext.Current.CancellationToken);
        Assert.False(removed.IsActive);
    }

    [Fact]
    public async Task QuestionThatComesBackIsReactivated()
    {
        await Sync(Q("gc-1"), Q("gc-2"));
        await Sync(Q("gc-1"));

        var result = await Sync(Q("gc-1"), Q("gc-2"));

        Assert.Equal(1, result.Updated);
        await using var db = _database.CreateContext();
        Assert.True((await db.Questions.SingleAsync(q => q.Id == "gc-2", TestContext.Current.CancellationToken)).IsActive);
    }

    [Fact]
    public async Task EmptyDirectoryIsRejectedInsteadOfDeactivatingEverything()
    {
        var dir = Directory.CreateTempSubdirectory("quizbot-empty-").FullName;
        try
        {
            await using var db = _database.CreateContext();
            var loader = new QuestionLoader(db, _time);

            var ex = await Assert.ThrowsAsync<QuestionValidationException>(() =>
                loader.LoadAsync(dir, TestContext.Current.CancellationToken));

            Assert.Contains("No questions found", ex.Message);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    private async Task<QuestionSyncResult> Sync(params QuestionDefinition[] questions)
    {
        await using var db = _database.CreateContext();
        return await new QuestionLoader(db, _time).SyncAsync(questions, TestContext.Current.CancellationToken);
    }

    private static QuestionDefinition Q(string id) => new(id, "gc", "core", $"Question {id}?", $"Answer {id}.", ["Follow-up?"], ["tag"]);
}
