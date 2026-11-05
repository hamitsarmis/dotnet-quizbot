using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuizBot.Options;
using QuizBot.Persistence;
using QuizBot.Questions;
using QuizBot.Scheduling;
using QuizBot.Telegram;
using QuizBot.Telegram.Commands;

namespace QuizBot;

public static class QuizBotServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything except the transport (<see cref="IQuizMessenger"/>), <see cref="TimeProvider"/> and
    /// <see cref="Random"/>. Program.cs supplies the production ones; tests supply fakes and a seed.
    /// </summary>
    public static IServiceCollection AddQuizBot(this IServiceCollection services)
    {
        // ValidateOnStart: a typo in .env stops the container at boot with a readable message rather than failing
        // on first use hours later. Subtlety: the host constructs hosted services before it runs the startup
        // validator, so a singleton that reads .Value in its constructor (QuizClock) triggers validation of that
        // options type a moment earlier. The message is the same; it just arrives one options type at a time.
        services.AddOptions<QuizOptions>()
            .BindConfiguration(QuizOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<TelegramOptions>()
            .BindConfiguration(TelegramOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<QuizDbContext>((provider, builder) =>
        {
            var dbPath = provider.GetRequiredService<IOptions<QuizOptions>>().Value.DbPath;
            builder.UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        });

        // Singletons hold no per-request state. The Random they share must be thread-safe (Random.Shared in
        // production): the scheduler and the update handler can draw from it concurrently.
        services.AddSingleton<QuizClock>();
        services.AddSingleton<ScheduleGenerator>();
        services.AddSingleton<QuestionSelector>();
        services.AddSingleton<IHeartbeat, FileHeartbeat>();

        // Scoped: everything that touches QuizDbContext lives and dies with one tick or one update.
        services.AddScoped<QuestionLoader>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<QuestionAsker>();
        services.AddScoped<BotStateStore>();
        services.AddScoped<CommandDispatcher>();
        services.AddScoped<GradeCallbackHandler>();
        services.AddScoped<IBotCommand, StartCommand>();
        services.AddScoped<IBotCommand, AskCommand>();
        services.AddScoped<IBotCommand, AnswerCommand>();
        services.AddScoped<IBotCommand, SkipCommand>();
        services.AddScoped<IBotCommand, TopicsCommand>();
        services.AddScoped<IBotCommand, StatsCommand>();
        services.AddScoped<IBotCommand, PauseCommand>();
        services.AddScoped<IBotCommand, ResumeCommand>();

        // Registration order is start order, and StartAsync calls are awaited one by one: the initializer
        // (migrate, load questions, plan today) finishes before either loop starts.
        services.AddHostedService<StartupInitializer>();
        services.AddHostedService<SchedulerService>();
        services.AddHostedService<UpdateHandlerService>();

        return services;
    }
}
