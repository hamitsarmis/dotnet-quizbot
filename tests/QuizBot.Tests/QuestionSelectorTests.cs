using QuizBot.Questions;
using static QuizBot.Questions.QuestionOutcome;

namespace QuizBot.Tests;

public sealed class QuestionSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<QuestionOutcome[], double> WeightCases() => new()
    {
        { [], 3.0 },
        { [DidntKnow], 2.5 },
        { [Skipped], 1.5 },
        { [Partially], 1.5 },
        { [KnewIt], 1.0 },
        { [KnewIt, KnewIt], 0.5 },
        { [KnewIt, KnewIt, KnewIt], 0.5 },
        { [KnewIt, DidntKnow], 1.0 },
        { [KnewIt, Skipped, KnewIt], 1.0 },
        { [DidntKnow, KnewIt, KnewIt], 2.5 },
        { [Partially, KnewIt, KnewIt], 1.5 },
    };

    [Theory]
    [MemberData(nameof(WeightCases))]
    public void WeightFollowsTheMostRecentOutcome(QuestionOutcome[] outcomesNewestFirst, double expected)
    {
        Assert.Equal(expected, QuestionSelector.Weight(outcomesNewestFirst));
    }

    [Fact]
    public void RecentlyAskedQuestionsAreExcluded()
    {
        var recent = Candidate("recent", askedAgo: TimeSpan.FromDays(1));
        var old = Candidate("old", askedAgo: TimeSpan.FromDays(4));
        var selector = new QuestionSelector(new Random(1));

        var picks = Enumerable.Range(0, 1000).Select(_ => selector.Select([recent, old], null, Now)!.Id).ToHashSet();

        Assert.Equal(["old"], picks);
    }

    [Fact]
    public void ExactlyThreeDaysAgoIsNoLongerRecent()
    {
        var boundary = Candidate("boundary", askedAgo: QuestionSelector.RecentlyAskedWindow);
        var recent = Candidate("recent", askedAgo: TimeSpan.FromHours(1));
        var selector = new QuestionSelector(new Random(1));

        var picks = Enumerable.Range(0, 200).Select(_ => selector.Select([boundary, recent], null, Now)!.Id).ToHashSet();

        Assert.Equal(["boundary"], picks);
    }

    [Fact]
    public void RecentQuestionsAreUsedWhenNothingElseIsLeft()
    {
        var a = Candidate("a", askedAgo: TimeSpan.FromHours(2));
        var b = Candidate("b", askedAgo: TimeSpan.FromHours(1));
        var selector = new QuestionSelector(new Random(1));

        var picks = Enumerable.Range(0, 200).Select(_ => selector.Select([a, b], null, Now)!.Id).ToHashSet();

        Assert.Equal(["a", "b"], picks.Order());
    }

    [Fact]
    public void TopicFilterIsExactAndCaseInsensitive()
    {
        var gc = Candidate("gc-1", topic: "gc");
        var async = Candidate("async-1", topic: "async");
        var asyncish = Candidate("asyncish-1", topic: "async-streams");
        var selector = new QuestionSelector(new Random(1));

        var picks = Enumerable.Range(0, 200)
            .Select(_ => selector.Select([gc, async, asyncish], "ASYNC", Now)!.Id)
            .ToHashSet();

        Assert.Equal(["async-1"], picks);
    }

    [Fact]
    public void TopicFilterRespectsRecencyInsideTheTopic()
    {
        var gcRecent = Candidate("gc-recent", topic: "gc", askedAgo: TimeSpan.FromHours(1));
        var gcOld = Candidate("gc-old", topic: "gc", askedAgo: TimeSpan.FromDays(10));
        var asyncFresh = Candidate("async-fresh", topic: "async");
        var selector = new QuestionSelector(new Random(1));

        var picks = Enumerable.Range(0, 200)
            .Select(_ => selector.Select([gcRecent, gcOld, asyncFresh], "gc", Now)!.Id)
            .ToHashSet();

        Assert.Equal(["gc-old"], picks);
    }

    [Fact]
    public void UnknownTopicOrEmptyPoolReturnsNull()
    {
        var selector = new QuestionSelector(new Random(1));

        Assert.Null(selector.Select([Candidate("gc-1", topic: "gc")], "kubernetes", Now));
        Assert.Null(selector.Select([], null, Now));
    }

    [Fact]
    public void IsDeterministicForASeededRandom()
    {
        QuestionCandidate[] pool = [.. Enumerable.Range(0, 20).Select(i => Candidate($"q-{i}"))];

        var first = Draw(new QuestionSelector(new Random(99)), pool, 50);
        var second = Draw(new QuestionSelector(new Random(99)), pool, 50);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DidntKnowIsDrawnMoreOftenThanKnewItOver10kDraws()
    {
        var didntKnow = Candidate("didnt-know", outcomes: [DidntKnow], askedAgo: TimeSpan.FromDays(5));
        var knewIt = Candidate("knew-it", outcomes: [KnewIt], askedAgo: TimeSpan.FromDays(5));
        var knewItTwice = Candidate("knew-it-twice", outcomes: [KnewIt, KnewIt], askedAgo: TimeSpan.FromDays(5));
        var selector = new QuestionSelector(new Random(2026));

        var counts = Draw(selector, [didntKnow, knewIt, knewItTwice], 10_000)
            .CountBy(id => id)
            .ToDictionary();

        // Expected shares are 2.5 : 1.0 : 0.5 of 4.0, i.e. 6250 / 2500 / 1250 draws.
        Assert.True(counts["didnt-know"] > counts["knew-it"] * 2, $"didnt-know={counts["didnt-know"]}, knew-it={counts["knew-it"]}");
        Assert.True(counts["knew-it"] > counts["knew-it-twice"] * 1.5, $"knew-it={counts["knew-it"]}, knew-it-twice={counts["knew-it-twice"]}");
        Assert.InRange(counts["didnt-know"], 6000, 6500);
    }

    private static List<string> Draw(QuestionSelector selector, IReadOnlyList<QuestionCandidate> pool, int draws) =>
        [.. Enumerable.Range(0, draws).Select(_ => selector.Select(pool, null, Now)!.Id)];

    private static QuestionCandidate Candidate(
        string id, string topic = "gc", TimeSpan? askedAgo = null, QuestionOutcome[]? outcomes = null) =>
        new(id, topic, askedAgo is { } ago ? Now - ago : null, outcomes ?? []);
}
