using Microsoft.Extensions.Options;
using QuizBot.Options;
using Telegram.Bot;

namespace QuizBot.Telegram;

public static class TelegramServiceCollectionExtensions
{
    /// <summary>Registers the real Telegram transport. The only registration that knows about <c>Telegram.Bot</c>.</summary>
    public static IServiceCollection AddTelegramMessenger(this IServiceCollection services)
    {
        services.AddSingleton<ITelegramBotClient>(provider =>
        {
            // One long-lived HttpClient for the process. Two deliberate choices:
            // - Not IHttpClientFactory: its default logging writes request URLs, and Telegram puts the bot token
            //   in the URL path.
            // - PooledConnectionLifetime: a singleton HttpClient otherwise keeps pooled connections (and the DNS
            //   answer behind them) forever; recycling them picks up DNS changes for api.telegram.org.
            var httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });
            var token = provider.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken;
            return new TelegramBotClient(token, httpClient);
        });
        services.AddSingleton<IQuizMessenger, TelegramMessenger>();
        return services;
    }
}
