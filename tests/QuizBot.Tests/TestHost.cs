using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using QuizBot.Persistence;
using QuizBot.Questions;
using QuizBot.Scheduling;
using QuizBot.Telegram;
using QuizBot.Tests.Fakes;

namespace QuizBot.Tests;

/// <summary>
/// The production composition (<see cref="QuizBotServiceCollectionExtensions.AddQuizBot"/>) with fakes for the
/// edges: <see cref="FakeTimeProvider"/>, a seeded <see cref="Random"/>, <see cref="FakeMessenger"/> and a
/// throwaway SQLite file. The provider validates scopes on build, exactly like Program.cs.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public const long AllowedChatId = 42;
    public const string BotToken = "123456:TEST-token_value";

    public static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"quizbot-tests-{Guid.NewGuid():N}.db");

    public TestHost(DateTimeOffset utcNow, IReadOnlyDictionary<string, string?>? settings = null, int seed = 1)
    {
        Time = new FakeTimeProvider(utcNow);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quiz:DbPath"] = _dbPath,
                ["Quiz:QuestionsDir"] = Path.Combine(AppContext.BaseDirectory, "questions"),
                ["Quiz:HeartbeatPath"] = Path.Combine(Path.GetTempPath(), "unused.heartbeat"),
                ["Telegram:BotToken"] = BotToken,
                ["Telegram:AllowedChatId"] = AllowedChatId.ToString(),
            })
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(new Random(seed));
        services.AddSingleton<IQuizMessenger>(Messenger);
        services.AddQuizBot();
        services.AddSingleton<IHeartbeat>(Heartbeat);

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public FakeTimeProvider Time { get; }

    public FakeMessenger Messenger { get; } = new();

    public FakeHeartbeat Heartbeat { get; } = new();

    public CollectingLoggerProvider Logs { get; } = new();

    public ServiceProvider Services { get; }

    public SchedulerService Scheduler => Services.GetServices<IHostedService>().OfType<SchedulerService>().Single();

    public UpdateHandlerService Updates => Services.GetServices<IHostedService>().OfType<UpdateHandlerService>().Single();

    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute, int second = 0) =>
        new DateTimeOffset(new DateTime(year, month, day, hour, minute, second), Istanbul.GetUtcOffset(new DateTime(year, month, day, hour, minute, second))).ToUniversalTime();

    /// <summary>Migrates the database and loads <paramref name="questions"/> as the question bank.</summary>
    public async Task InitializeAsync(params QuestionDefinition[] questions)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<QuizDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<QuestionLoader>().SyncAsync(questions, CancellationToken.None);
    }

    public async Task<T> WithDbAsync<T>(Func<QuizDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<QuizDbContext>());
    }

    public async Task WithDbAsync(Func<QuizDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<QuizDbContext>());
    }

    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    /// <summary>Adds pending slots for the local day of the first slot, which stops the scheduler from generating one.</summary>
    public Task AddSlotsAsync(params DateTimeOffset[] fireTimesUtc) => WithDbAsync(async db =>
    {
        foreach (var fireAt in fireTimesUtc)
        {
            db.ScheduledSlots.Add(new ScheduledSlot
            {
                LocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fireAt, Istanbul).DateTime),
                FireAtUtc = fireAt,
                Status = SlotStatus.Pending,
            });
        }

        await db.SaveChangesAsync();
    });

    public Task SetPausedAsync(bool paused) =>
        WithScopeAsync(sp => sp.GetRequiredService<BotStateStore>().SetPausedAsync(paused, CancellationToken.None));

    public static QuestionDefinition Question(string id, string topic = "gc", string answer = "The answer.") =>
        new(id, topic, "core", $"What about {id}?", answer, ["A follow-up?"], ["tag"]);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();

        // Pooled connections keep the file open; release them so the file can be deleted.
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
