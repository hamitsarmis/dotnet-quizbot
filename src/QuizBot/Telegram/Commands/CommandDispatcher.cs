namespace QuizBot.Telegram.Commands;

/// <summary>
/// Routes one update from the allowed chat to its command. Scoped: created inside the per-update scope, so
/// the commands it resolves share that update's <c>QuizDbContext</c>.
/// </summary>
public sealed partial class CommandDispatcher(
    IEnumerable<IBotCommand> commands,
    GradeCallbackHandler grades,
    IQuizMessenger messenger,
    ILogger<CommandDispatcher> logger)
{
    public Task DispatchAsync(IncomingUpdate update, CancellationToken cancellationToken) => update switch
    {
        IncomingMessage message => HandleMessageAsync(message, cancellationToken),
        IncomingCallback callback => HandleCallbackAsync(callback, cancellationToken),
        _ => Task.CompletedTask,
    };

    private async Task HandleMessageAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        var (name, arguments) = Parse(message.Text);
        var command = name is null ? null : commands.FirstOrDefault(c => c.Name == name);
        if (command is null)
        {
            Log.UnknownInput(logger);
            await messenger.SendAsync(new OutgoingMessage(BotTexts.OneLineHelp), cancellationToken);
            return;
        }

        Log.CommandReceived(logger, command.Name);
        await command.ExecuteAsync(arguments, cancellationToken);
    }

    private async Task HandleCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken)
    {
        if (GradeCallbackHandler.CanHandle(callback.Data))
        {
            await grades.HandleAsync(callback, cancellationToken);
            return;
        }

        // Every callback must be answered, or the Telegram client keeps showing a spinner on the button.
        await messenger.AnswerCallbackAsync(callback.CallbackId, null, cancellationToken);
    }

    /// <summary>
    /// Splits <c>/Ask@QuizBot gc</c> into <c>("ask", "gc")</c>. Free text has no command name.
    /// </summary>
    internal static (string? Name, string Arguments) Parse(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/') || trimmed.Length == 1)
        {
            return (null, "");
        }

        var spaceIndex = trimmed.IndexOfAny([' ', '\n', '\t']);
        var token = spaceIndex < 0 ? trimmed[1..] : trimmed[1..spaceIndex];
        var arguments = spaceIndex < 0 ? "" : trimmed[(spaceIndex + 1)..].Trim();

        // In groups Telegram appends the bot's username: /ask@MyQuizBot.
        var atIndex = token.IndexOf('@');
        if (atIndex >= 0)
        {
            token = token[..atIndex];
        }

        return (token.ToLowerInvariant(), arguments);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Handling /{Command}")]
        public static partial void CommandReceived(ILogger logger, string command);

        // The text itself is never logged: it is the user's private input.
        [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "Unknown command or free text; replied with help")]
        public static partial void UnknownInput(ILogger logger);
    }
}
