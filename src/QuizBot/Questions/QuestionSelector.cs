namespace QuizBot.Questions;

/// <summary>What happened the last times a question was asked. Asks without a grade or a skip produce no outcome.</summary>
public enum QuestionOutcome
{
    KnewIt,
    Partially,
    DidntKnow,
    Skipped,
}

/// <summary>An active question the selector may pick, with its history.</summary>
/// <param name="OutcomesNewestFirst">Outcomes of previous asks, most recent first.</param>
public sealed record QuestionCandidate(
    string Id,
    string Topic,
    DateTimeOffset? LastAskedAtUtc,
    IReadOnlyList<QuestionOutcome> OutcomesNewestFirst);

/// <summary>
/// Weighted random question selection, biased toward what was answered badly. Pure: no clock and no I/O,
/// so it is tested with a seeded <see cref="Random"/> and fixed instants.
/// </summary>
public sealed class QuestionSelector(Random random)
{
    public static readonly TimeSpan RecentlyAskedWindow = TimeSpan.FromDays(3);

    /// <summary>
    /// Weight from the most recent outcome. A question that was asked but never graded or skipped has
    /// no outcome and counts as never asked: there is no signal that it was learned.
    /// </summary>
    public static double Weight(IReadOnlyList<QuestionOutcome> outcomesNewestFirst) => outcomesNewestFirst switch
    {
        [] => 3.0,
        [QuestionOutcome.DidntKnow, ..] => 2.5,
        [QuestionOutcome.Skipped, ..] => 1.5,
        [QuestionOutcome.Partially, ..] => 1.5,
        [QuestionOutcome.KnewIt, QuestionOutcome.KnewIt, ..] => 0.5,
        [QuestionOutcome.KnewIt, ..] => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(outcomesNewestFirst), outcomesNewestFirst[0], "Unknown outcome."),
    };

    /// <summary>
    /// Picks one question from <paramref name="activeQuestions"/>, optionally restricted to <paramref name="topic"/>
    /// (exact, case-insensitive). Questions asked within <see cref="RecentlyAskedWindow"/> are excluded unless
    /// that would leave nothing to pick.
    /// </summary>
    /// <returns>The chosen question, or <c>null</c> when no question matches the topic.</returns>
    public QuestionCandidate? Select(IReadOnlyList<QuestionCandidate> activeQuestions, string? topic, DateTimeOffset nowUtc)
    {
        var pool = topic is null
            ? activeQuestions
            : activeQuestions.Where(q => string.Equals(q.Topic, topic, StringComparison.OrdinalIgnoreCase)).ToList();

        if (pool.Count == 0)
        {
            return null;
        }

        var notRecent = pool
            .Where(q => q.LastAskedAtUtc is not { } askedAt || nowUtc - askedAt >= RecentlyAskedWindow)
            .ToList();
        if (notRecent.Count > 0)
        {
            pool = notRecent;
        }

        var weights = pool.Select(q => Weight(q.OutcomesNewestFirst)).ToArray();
        var roll = random.NextDouble() * weights.Sum();
        for (var i = 0; i < pool.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0)
            {
                return pool[i];
            }
        }

        // Floating-point rounding can leave a tiny remainder after the last weight.
        return pool[^1];
    }
}
