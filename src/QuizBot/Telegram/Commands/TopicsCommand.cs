using System.Text;
using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Questions;

namespace QuizBot.Telegram.Commands;

/// <summary><c>/topics</c>: active topics, question counts and pass rate (Knew it ÷ graded answers).</summary>
public sealed class TopicsCommand(QuestionAsker asker, QuizDbContext db, IQuizMessenger messenger) : IBotCommand
{
    public string Name => "topics";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        var topics = await asker.GetTopicsAsync(cancellationToken);
        var grades = await db.AskedQuestions
            .Where(a => a.Grade != null)
            .Select(a => new { a.Question.Topic, a.Grade })
            .ToListAsync(cancellationToken);
        var gradesByTopic = grades.ToLookup(g => g.Topic);

        var html = new StringBuilder("<b>Topics</b> (pass rate = Knew it ÷ graded)");
        foreach (var (topic, count) in topics)
        {
            var graded = gradesByTopic[topic].ToList();
            var passRate = graded.Count == 0
                ? "not graded yet"
                : $"{100.0 * graded.Count(g => g.Grade == Grade.KnewIt) / graded.Count:0}% of {graded.Count} graded";
            html.Append($"\n• <b>{QuestionFormatter.Escape(topic)}</b>: {count} questions · {passRate}");
        }

        await messenger.SendAsync(new OutgoingMessage(html.ToString()), cancellationToken);
    }
}
