namespace QuizBot.Telegram.Commands;

/// <summary>A slash command. Implementations are scoped and reply through <see cref="IQuizMessenger"/>.</summary>
public interface IBotCommand
{
    /// <summary>The command name without the slash, lowercase, e.g. <c>ask</c>.</summary>
    string Name { get; }

    /// <param name="arguments">Text after the command, trimmed; empty when there is none.</param>
    Task ExecuteAsync(string arguments, CancellationToken cancellationToken);
}

public static class BotTexts
{
    public const string OneLineHelp = "Try /ask [topic], /answer, /skip, /topics, /stats, /pause or /resume.";

    public const string FullHelp = """
        /ask [topic] — a question right now (does not use up a scheduled one)
        /answer — reveal the model answer, then grade yourself
        /skip — skip the open question
        /topics — topics, question counts and pass rates
        /stats — totals, the last 7 days and your streak
        /pause, /resume — stop or restart scheduled questions
        """;
}
