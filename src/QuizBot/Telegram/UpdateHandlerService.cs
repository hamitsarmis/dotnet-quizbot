using Microsoft.Extensions.Options;
using QuizBot.Options;
using QuizBot.Telegram.Commands;

namespace QuizBot.Telegram;

/// <summary>
/// Long-polls the messenger and dispatches each update inside its own DI scope. Long polling means the bot
/// only makes outbound requests: no public endpoint, no webhook, and the VPS firewall stays closed.
/// </summary>
public sealed partial class UpdateHandlerService(
    IQuizMessenger messenger,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateHandlerService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly long _allowedChatId = options.Value.AllowedChatId;

    // Only touched by the single polling loop, so no locking is needed.
    private readonly HashSet<long> _warnedChatIds = [];
    private long _offset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<IncomingUpdate> updates;
            try
            {
                updates = await messenger.GetUpdatesAsync(_offset, stoppingToken);
                backoff = InitialBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Network blips and Telegram outages are expected over months of uptime; back off and keep polling.
                Log.PollingFailed(logger, ex, backoff);
                try
                {
                    await Task.Delay(backoff, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                continue;
            }

            foreach (var update in updates)
            {
                await HandleUpdateAsync(update, stoppingToken);

                // The offset only moves past an update after it was handled. If the process dies mid-batch, the
                // next poll asks for the same offset and Telegram redelivers the rest: at-least-once handling.
                _offset = update.UpdateId + 1;
            }
        }
    }

    internal async Task HandleUpdateAsync(IncomingUpdate update, CancellationToken stoppingToken)
    {
        long? chatId = update switch
        {
            IncomingMessage message => message.ChatId,
            IncomingCallback callback => callback.ChatId,
            _ => null,
        };

        if (chatId is null)
        {
            Log.UnsupportedUpdate(logger, update.UpdateId);
            return;
        }

        if (chatId != _allowedChatId)
        {
            // Never reply: a reply would confirm to a stranger that the bot exists and is listening.
            if (_warnedChatIds.Add(chatId.Value))
            {
                Log.UnknownChat(logger, chatId.Value);
            }

            return;
        }

        try
        {
            // One scope per update: the commands get a fresh QuizDbContext, disposed when the update is done.
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CommandDispatcher>().DispatchAsync(update, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Log and move on. Rethrowing would stop the offset from advancing, and one poison update
            // would then be redelivered and fail forever.
            Log.UpdateFailed(logger, ex, update.UpdateId);
            await TryReportFailureAsync(update, stoppingToken);
        }
    }

    private async Task TryReportFailureAsync(IncomingUpdate update, CancellationToken stoppingToken)
    {
        try
        {
            if (update is IncomingCallback callback)
            {
                await messenger.AnswerCallbackAsync(callback.CallbackId, "Something went wrong.", stoppingToken);
            }
            else
            {
                await messenger.SendAsync(new OutgoingMessage("Something went wrong; the details are in the logs."), stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            Log.FailureReportFailed(logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "Ignoring updates from unknown chat {ChatId}; further updates from it are ignored silently")]
        public static partial void UnknownChat(ILogger logger, long chatId);

        [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "Polling for updates failed; retrying in {Backoff}")]
        public static partial void PollingFailed(ILogger logger, Exception exception, TimeSpan backoff);

        [LoggerMessage(EventId = 402, Level = LogLevel.Error, Message = "Handling update {UpdateId} failed")]
        public static partial void UpdateFailed(ILogger logger, Exception exception, long updateId);

        [LoggerMessage(EventId = 403, Level = LogLevel.Debug, Message = "Ignoring unsupported update {UpdateId}")]
        public static partial void UnsupportedUpdate(ILogger logger, long updateId);

        [LoggerMessage(EventId = 404, Level = LogLevel.Warning, Message = "Could not tell the user that an update failed")]
        public static partial void FailureReportFailed(ILogger logger, Exception exception);
    }
}
