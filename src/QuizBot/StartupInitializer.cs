using System.Globalization;
using System.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuizBot.Options;
using QuizBot.Persistence;
using QuizBot.Questions;
using QuizBot.Scheduling;

namespace QuizBot;

/// <summary>
/// Brings the database and question bank up to date before the loops start. Hosted services start in
/// registration order and each <c>StartAsync</c> is awaited, so registering this first means the scheduler and
/// the update handler never see an unmigrated database. Any failure here fails startup (fail fast).
/// </summary>
public sealed partial class StartupInitializer(
    IServiceScopeFactory scopeFactory,
    IOptions<QuizOptions> options,
    QuizClock clock,
    ILogger<StartupInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var quiz = options.Value;
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        await MigrateAsync(services.GetRequiredService<QuizDbContext>(), quiz.DbPath, cancellationToken);

        var sync = await services.GetRequiredService<QuestionLoader>().LoadAsync(quiz.QuestionsDir, cancellationToken);
        Log.QuestionsLoaded(logger, sync.Active, sync.Added, sync.Updated, sync.Deactivated);

        var scheduleService = services.GetRequiredService<ScheduleService>();
        var slots = await scheduleService.EnsureTodayAsync(cancellationToken);

        Log.Started(
            logger,
            clock.TimeZone.Id,
            clock.ToLocal(clock.UtcNow).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
            scheduleService.Describe(slots),
            sync.Active,
            GCSettings.IsServerGC ? "Server" : "Workstation");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task MigrateAsync(QuizDbContext db, string dbPath, CancellationToken cancellationToken)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(dbPath)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            // Applying migrations at startup is acceptable only because exactly one instance of this bot ever runs.
            // In a multi-instance API it would be wrong: instances race to apply the same migration, and a long
            // migration holds locks while half the fleet runs old code. There, migrations are a separate deploy step
            // (an EF migration bundle or an idempotent SQL script) that runs once before the new version rolls out.
            await db.Database.MigrateAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 14 /* SQLITE_CANTOPEN */ or 8 /* SQLITE_READONLY */)
        {
            throw new InvalidOperationException(
                $"Cannot write the database at '{dbPath}'. If its directory is a bind mount, make it writable by the " +
                "container user (UID 1654), e.g. `sudo chown 1654:1654 ./data`.", ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "QuizBot started. Time zone {TimeZone}, local time {LocalTime}, today's slots: {Slots}; {ActiveQuestions} active questions; {GcMode} GC")]
        public static partial void Started(ILogger logger, string timeZone, string localTime, string slots, int activeQuestions, string gcMode);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Question bank synced: {Active} active ({Added} added, {Updated} updated, {Deactivated} deactivated)")]
        public static partial void QuestionsLoaded(ILogger logger, int active, int added, int updated, int deactivated);
    }
}
