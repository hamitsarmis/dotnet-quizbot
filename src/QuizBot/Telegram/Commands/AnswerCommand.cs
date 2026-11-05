using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Questions;

namespace QuizBot.Telegram.Commands;

/// <summary>
/// <c>/answer</c>: reveals the most recent question that is still unrevealed (skipped ones count as closed);
/// when there is none, re-reveals the most recent question. Ungraded reveals get the grading keyboard.
/// </summary>
public sealed class AnswerCommand(QuizDbContext db, IQuizMessenger messenger, TimeProvider timeProvider) : IBotCommand
{
    public string Name => "answer";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        var newestFirst = db.AskedQuestions
            .Include(a => a.Question)
            .OrderByDescending(a => a.AskedAtUtc)
            .ThenByDescending(a => a.Id);

        var target = await newestFirst
                .Where(a => a.RevealedAtUtc == null && a.SkippedAtUtc == null && a.Grade == null)
                .FirstOrDefaultAsync(cancellationToken)
            ?? await newestFirst.FirstOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            await messenger.SendAsync(new OutgoingMessage($"No question to reveal yet. {BotTexts.OneLineHelp}"), cancellationToken);
            return;
        }

        var html = QuestionFormatter.FormatReveal(target.Question);
        if (target.Grade is { } grade)
        {
            html += $"\n\n{GradeCallbackHandler.GradeLine(grade)}";
        }

        await messenger.SendAsync(
            new OutgoingMessage(
                html,
                ReplyToMessageId: target.TelegramMessageId,
                Buttons: target.Grade is null ? GradeCallbackHandler.Buttons(target.Id) : null),
            cancellationToken);

        target.RevealedAtUtc ??= timeProvider.GetUtcNow();

        // The answer is already on the user's screen; shutdown must not cancel recording that (same rule as the
        // scheduler's post-send write).
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
