using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Questions;

namespace QuizBot.Telegram.Commands;

/// <summary><c>/skip</c>: closes the latest question without a grade. It then weighs as "skipped" in selection.</summary>
public sealed class SkipCommand(QuizDbContext db, IQuizMessenger messenger, TimeProvider timeProvider) : IBotCommand
{
    public string Name => "skip";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        var latest = await db.AskedQuestions
            .OrderByDescending(a => a.AskedAtUtc)
            .ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null || latest.Grade is not null || latest.SkippedAtUtc is not null)
        {
            await messenger.SendAsync(new OutgoingMessage($"Nothing to skip. {BotTexts.OneLineHelp}"), cancellationToken);
            return;
        }

        latest.SkippedAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await messenger.SendAsync(
            new OutgoingMessage($"⏭ Skipped <code>{QuestionFormatter.Escape(latest.QuestionId)}</code>."),
            cancellationToken);
    }
}
