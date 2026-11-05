using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Questions;

namespace QuizBot.Telegram.Commands;

/// <summary>Records a self-grade from the inline keyboard under a revealed answer.</summary>
public sealed partial class GradeCallbackHandler(
    QuizDbContext db,
    IQuizMessenger messenger,
    TimeProvider timeProvider,
    ILogger<GradeCallbackHandler> logger)
{
    private const string Prefix = "g:";

    /// <summary>Compact callback data: Telegram allows at most 64 bytes, e.g. <c>g:123:k</c>.</summary>
    public static string CallbackData(int askedQuestionId, Grade grade) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{askedQuestionId}:{Code(grade)}");

    public static IReadOnlyList<InlineButton> Buttons(int askedQuestionId) =>
    [
        new(Label(Grade.KnewIt), CallbackData(askedQuestionId, Grade.KnewIt)),
        new(Label(Grade.Partially), CallbackData(askedQuestionId, Grade.Partially)),
        new(Label(Grade.DidntKnow), CallbackData(askedQuestionId, Grade.DidntKnow)),
    ];

    public static string Label(Grade grade) => grade switch
    {
        Grade.KnewIt => "✅ Knew it",
        Grade.Partially => "🟡 Partially",
        Grade.DidntKnow => "❌ Didn't know",
        _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, null),
    };

    public static bool CanHandle(string callbackData) => callbackData.StartsWith(Prefix, StringComparison.Ordinal);

    public static string GradeLine(Grade grade) => $"<b>Your grade:</b> {Label(grade)}";

    /// <summary>
    /// Replacement text for the message that carried the keyboard. A long reveal is sent in parts with the keyboard
    /// on the last one, so this is only that last part, with the grade where the keyboard was.
    /// </summary>
    public static string GradedLastPartHtml(StoredQuestion question, Grade grade)
    {
        var lastPart = MessageSplitter.Split(QuestionFormatter.FormatReveal(question))[^1];
        return $"{lastPart}\n\n{GradeLine(grade)}";
    }

    public async Task HandleAsync(IncomingCallback callback, CancellationToken cancellationToken)
    {
        if (!TryParse(callback.Data, out var askedQuestionId, out var grade))
        {
            await messenger.AnswerCallbackAsync(callback.CallbackId, null, cancellationToken);
            return;
        }

        var asked = await db.AskedQuestions
            .Include(a => a.Question)
            .SingleOrDefaultAsync(a => a.Id == askedQuestionId, cancellationToken);

        if (asked is null || asked.Grade is not null)
        {
            // Stale or duplicate: a double tap, or a button on a message graded earlier. Acknowledge so the client
            // stops its spinner, and change nothing.
            Log.StaleGrade(logger, askedQuestionId);
            var text = asked?.Grade is { } existing ? $"Already graded: {Label(existing)}" : null;
            await messenger.AnswerCallbackAsync(callback.CallbackId, text, cancellationToken);
            return;
        }

        asked.Grade = grade;
        asked.GradedAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        Log.Graded(logger, asked.QuestionId, grade);

        // The grade is committed; what follows is cosmetic. Each step gets its own try so that one failing
        // (Telegram rejects acks for queries older than a few minutes, e.g. a tap made while the bot was down)
        // neither skips the other nor reports the whole update as failed.
        try
        {
            await messenger.AnswerCallbackAsync(callback.CallbackId, $"Saved: {Label(grade)}", cancellationToken);
        }
        catch (QuizMessengerException ex)
        {
            Log.AcknowledgeFailed(logger, ex);
        }

        try
        {
            await messenger.EditAsync(callback.MessageId, GradedLastPartHtml(asked.Question, grade), cancellationToken);
        }
        catch (QuizMessengerException ex)
        {
            // A message that can no longer be edited (too old, deleted) only keeps its stale keyboard.
            Log.EditFailed(logger, ex, callback.MessageId);
        }
    }

    internal static bool TryParse(string data, out int askedQuestionId, out Grade grade)
    {
        askedQuestionId = 0;
        grade = default;
        var parts = data.Split(':');
        if (parts is not ["g", var id, var code] || !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out askedQuestionId))
        {
            return false;
        }

        Grade? parsed = code switch
        {
            "k" => Grade.KnewIt,
            "p" => Grade.Partially,
            "d" => Grade.DidntKnow,
            _ => null,
        };
        grade = parsed.GetValueOrDefault();
        return parsed is not null;
    }

    private static char Code(Grade grade) => grade switch
    {
        Grade.KnewIt => 'k',
        Grade.Partially => 'p',
        Grade.DidntKnow => 'd',
        _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, null),
    };

    private static partial class Log
    {
        [LoggerMessage(EventId = 310, Level = LogLevel.Information, Message = "Recorded grade {Grade} for question {QuestionId}")]
        public static partial void Graded(ILogger logger, string questionId, Grade grade);

        [LoggerMessage(EventId = 311, Level = LogLevel.Information, Message = "Ignored stale or duplicate grade for asked question {AskedQuestionId}")]
        public static partial void StaleGrade(ILogger logger, int askedQuestionId);

        [LoggerMessage(EventId = 312, Level = LogLevel.Warning, Message = "Grade saved, but message {MessageId} could not be edited")]
        public static partial void EditFailed(ILogger logger, Exception exception, long messageId);

        [LoggerMessage(EventId = 313, Level = LogLevel.Warning, Message = "Grade saved, but the button press could not be acknowledged")]
        public static partial void AcknowledgeFailed(ILogger logger, Exception exception);
    }
}
