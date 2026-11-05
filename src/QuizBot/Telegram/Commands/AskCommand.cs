using QuizBot.Persistence;
using QuizBot.Questions;

namespace QuizBot.Telegram.Commands;

/// <summary><c>/ask [topic]</c>: a question now. Independent of the schedule, so it never uses up a slot.</summary>
public sealed class AskCommand(QuestionAsker asker, QuizDbContext db, IQuizMessenger messenger) : IBotCommand
{
    public string Name => "ask";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        var topic = string.IsNullOrWhiteSpace(arguments) ? null : arguments;
        if (topic is not null)
        {
            var topics = await asker.GetTopicsAsync(cancellationToken);
            if (!topics.Any(t => string.Equals(t.Topic, topic, StringComparison.OrdinalIgnoreCase)))
            {
                var known = string.Join(", ", topics.Select(t => t.Topic));
                await messenger.SendAsync(
                    new OutgoingMessage($"Unknown topic <b>{QuestionFormatter.Escape(topic)}</b>. Topics: {QuestionFormatter.Escape(known)}."),
                    cancellationToken);
                return;
            }
        }

        if (await asker.AskAsync(topic, QuestionSource.Manual, cancellationToken) is null)
        {
            await messenger.SendAsync(new OutgoingMessage("There are no active questions."), cancellationToken);
            return;
        }

        // Telegram already delivered the question. Without this row /answer would reveal the previous question, so
        // shutdown must not cancel the write (same rule as the scheduler's post-send write).
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
