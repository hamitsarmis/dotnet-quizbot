using Microsoft.Extensions.Options;
using QuizBot.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace QuizBot.Telegram;

/// <summary>
/// <see cref="IQuizMessenger"/> on top of <c>Telegram.Bot</c>. Deliberately thin: it maps types and sanitises
/// errors, and every decision about what to send lives in code that is tested against a fake.
/// </summary>
public sealed partial class TelegramMessenger(
    ITelegramBotClient bot,
    IOptions<TelegramOptions> options,
    ILogger<TelegramMessenger> logger) : IQuizMessenger
{
    /// <summary>Server-side wait per getUpdates call. Well below HttpClient's 100 s default timeout.</summary>
    private const int LongPollTimeoutSeconds = 30;

    private static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery];
    private static readonly LinkPreviewOptions NoLinkPreview = new() { IsDisabled = true };

    private readonly ChatId _chatId = options.Value.AllowedChatId;
    private readonly string _token = options.Value.BotToken;

    public async Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        var updates = await CallAsync(
            () => bot.GetUpdates(
                offset: checked((int)offset),
                timeout: LongPollTimeoutSeconds,
                allowedUpdates: AllowedUpdates,
                cancellationToken: cancellationToken),
            cancellationToken);

        return updates.Select(Map).ToList();
    }

    public async Task<long> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        var parts = MessageSplitter.Split(message.Html);
        var lastMessageId = 0L;
        for (var i = 0; i < parts.Count; i++)
        {
            var isFirst = i == 0;
            var isLast = i == parts.Count - 1;
            var sent = await CallAsync(
                () => bot.SendMessage(
                    _chatId,
                    parts[i],
                    parseMode: ParseMode.Html,
                    replyParameters: isFirst && message.ReplyToMessageId is { } replyTo
                        ? new ReplyParameters { MessageId = checked((int)replyTo), AllowSendingWithoutReply = true }
                        : null,
                    replyMarkup: isLast ? Keyboard(message.Buttons) : null,
                    linkPreviewOptions: NoLinkPreview,
                    cancellationToken: cancellationToken),
                cancellationToken);
            lastMessageId = sent.Id;
        }

        Log.MessageSent(logger, parts.Count);
        return lastMessageId;
    }

    public Task EditAsync(long messageId, string html, CancellationToken cancellationToken) =>
        CallAsync(
            // Omitting replyMarkup on an edit removes the message's inline keyboard.
            () => bot.EditMessageText(
                _chatId,
                checked((int)messageId),
                html,
                parseMode: ParseMode.Html,
                linkPreviewOptions: NoLinkPreview,
                cancellationToken: cancellationToken),
            cancellationToken);

    public Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken cancellationToken) =>
        CallAsync(
            async () =>
            {
                await bot.AnswerCallbackQuery(callbackId, text, cancellationToken: cancellationToken);
                return true;
            },
            cancellationToken);

    private static IncomingUpdate Map(Update update) => update switch
    {
        { Message: { } message } => new IncomingMessage(update.Id, message.Chat.Id, message.Text ?? ""),
        { CallbackQuery: { } query } => new IncomingCallback(
            update.Id,
            query.Message?.Chat.Id ?? query.From.Id,
            query.Id,
            query.Message?.Id ?? 0,
            query.Data ?? ""),
        _ => new UnsupportedUpdate(update.Id),
    };

    private static InlineKeyboardMarkup? Keyboard(IReadOnlyList<InlineButton>? buttons) =>
        buttons is null or []
            ? null
            : new InlineKeyboardMarkup(buttons.Select(b => InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackData)));

    private async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Every Telegram API URL contains the bot token (https://api.telegram.org/bot<token>/sendMessage), so a
            // transport exception may carry it in its message or an inner exception. Rethrow a sanitised exception
            // and deliberately drop the original, so nothing upstream can log the token by accident.
            throw new QuizMessengerException(Redact(Describe(ex), _token));
        }
    }

    private static string Describe(Exception exception)
    {
        var description = exception is ApiRequestException api
            ? $"Telegram API error {api.ErrorCode}: {api.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";

        return exception.InnerException is { } inner ? $"{description} <- {Describe(inner)}" : description;
    }

    /// <summary>Replaces the token, and its secret half on its own, with <c>***</c>.</summary>
    internal static string Redact(string text, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return text;
        }

        text = text.Replace(token, "***", StringComparison.Ordinal);
        var secret = token[(token.IndexOf(':') + 1)..];
        return secret.Length >= 8 ? text.Replace(secret, "***", StringComparison.Ordinal) : text;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 200, Level = LogLevel.Debug, Message = "Sent a message in {Parts} part(s)")]
        public static partial void MessageSent(ILogger logger, int parts);
    }
}
