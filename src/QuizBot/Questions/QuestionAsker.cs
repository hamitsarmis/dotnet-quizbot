using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Telegram;

namespace QuizBot.Questions;

/// <summary>
/// Picks, sends and records a question. Shared by the scheduler and <c>/ask</c>.
/// </summary>
public sealed class QuestionAsker(QuizDbContext db, QuestionSelector selector, IQuizMessenger messenger, TimeProvider timeProvider)
{
    /// <summary>
    /// Selects a question (optionally within <paramref name="topic"/>), sends it and adds the resulting
    /// <see cref="AskedQuestion"/> to the current unit of work. The caller calls <c>SaveChangesAsync</c>, so it
    /// can commit the ask together with its own bookkeeping (the scheduler marks the slot sent in the same save).
    /// </summary>
    /// <returns>The new ask, or <c>null</c> when no active question matches.</returns>
    public async Task<AskedQuestion?> AskAsync(string? topic, QuestionSource source, CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(cancellationToken);
        var pick = selector.Select(candidates, topic, timeProvider.GetUtcNow());
        if (pick is null)
        {
            return null;
        }

        var question = await db.Questions.SingleAsync(q => q.Id == pick.Id, cancellationToken);
        var messageId = await messenger.SendAsync(new OutgoingMessage(QuestionFormatter.FormatQuestion(question)), cancellationToken);

        var asked = new AskedQuestion
        {
            QuestionId = question.Id,
            Question = question,
            AskedAtUtc = timeProvider.GetUtcNow(),
            Source = source,
            TelegramMessageId = messageId,
        };
        db.AskedQuestions.Add(asked);
        return asked;
    }

    /// <summary>Active topics with their active question counts, alphabetically.</summary>
    public async Task<IReadOnlyList<(string Topic, int Count)>> GetTopicsAsync(CancellationToken cancellationToken)
    {
        var topics = await db.Questions
            .Where(q => q.IsActive)
            .GroupBy(q => q.Topic)
            .Select(g => new { Topic = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return topics.OrderBy(t => t.Topic, StringComparer.Ordinal).Select(t => (t.Topic, t.Count)).ToList();
    }

    internal async Task<IReadOnlyList<QuestionCandidate>> LoadCandidatesAsync(CancellationToken cancellationToken)
    {
        // A single user produces a few asks per day, so loading the history and grouping in memory stays cheap
        // and keeps the weighting rules in the pure QuestionSelector rather than in SQL.
        var questions = await db.Questions
            .Where(q => q.IsActive)
            .Select(q => new { q.Id, q.Topic })
            .ToListAsync(cancellationToken);

        var history = await db.AskedQuestions
            .Where(a => a.Question.IsActive)
            .OrderByDescending(a => a.AskedAtUtc)
            .Select(a => new { a.QuestionId, a.AskedAtUtc, a.Grade, a.SkippedAtUtc })
            .ToListAsync(cancellationToken);

        // ToLookup keeps source order inside each group, so every group stays newest first.
        var asksByQuestion = history.ToLookup(a => a.QuestionId);

        return questions
            .Select(q =>
            {
                var asks = asksByQuestion[q.Id].ToList();
                var outcomes = asks
                    .Select(a => ToOutcome(a.Grade, a.SkippedAtUtc))
                    .OfType<QuestionOutcome>()
                    .ToList();
                return new QuestionCandidate(q.Id, q.Topic, asks.FirstOrDefault()?.AskedAtUtc, outcomes);
            })
            .ToList();
    }

    /// <summary>A grade wins over a skip: skipping and later grading the same ask records what was learned.</summary>
    private static QuestionOutcome? ToOutcome(Grade? grade, DateTimeOffset? skippedAtUtc) => grade switch
    {
        Grade.KnewIt => QuestionOutcome.KnewIt,
        Grade.Partially => QuestionOutcome.Partially,
        Grade.DidntKnow => QuestionOutcome.DidntKnow,
        null when skippedAtUtc is not null => QuestionOutcome.Skipped,
        _ => null,
    };
}
