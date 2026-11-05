namespace QuizBot.Telegram;

/// <summary>
/// The bot's port to its chat transport. Everything outside <c>Telegram/</c> talks to this interface, never to
/// <c>Telegram.Bot</c>, so tests use a fake instead of mocking a third-party client.
/// There is no chat id parameter anywhere: messages can only go to the configured chat, so replying to a
/// stranger is impossible by construction.
/// </summary>
public interface IQuizMessenger
{
    /// <summary>
    /// Long-polls for updates with an id of at least <paramref name="offset"/>. Asking for a higher offset
    /// confirms every earlier update. Returns an empty list when the poll times out.
    /// </summary>
    Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken);

    /// <summary>Sends HTML to the configured chat, splitting long text. Returns the id of the last message sent.</summary>
    Task<long> SendAsync(OutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>Replaces the text of a sent message and removes its inline keyboard.</summary>
    Task EditAsync(long messageId, string html, CancellationToken cancellationToken);

    /// <summary>Acknowledges a button press so the client stops showing a spinner.</summary>
    Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken cancellationToken);
}

/// <param name="Html">Telegram HTML: only &lt;b&gt;, &lt;i&gt;, &lt;code&gt; and escaped text.</param>
/// <param name="ReplyToMessageId">Shows the message as a reply, for example an answer to its question.</param>
/// <param name="Buttons">One row of inline buttons attached to the (last) message.</param>
public sealed record OutgoingMessage(string Html, long? ReplyToMessageId = null, IReadOnlyList<InlineButton>? Buttons = null);

public sealed record InlineButton(string Text, string CallbackData);

public abstract record IncomingUpdate(long UpdateId);

public sealed record IncomingMessage(long UpdateId, long ChatId, string Text) : IncomingUpdate(UpdateId);

public sealed record IncomingCallback(long UpdateId, long ChatId, string CallbackId, long MessageId, string Data)
    : IncomingUpdate(UpdateId);

/// <summary>An update kind the bot does not handle. It still has to be confirmed so it is not redelivered.</summary>
public sealed record UnsupportedUpdate(long UpdateId) : IncomingUpdate(UpdateId);

/// <summary>
/// Thrown by messenger implementations for transport failures. Its message is safe to log: implementations
/// must strip secrets (the bot token is part of every Telegram API URL).
/// </summary>
public sealed class QuizMessengerException(string message) : Exception(message);
