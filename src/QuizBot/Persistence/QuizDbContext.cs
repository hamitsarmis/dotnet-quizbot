using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using QuizBot.Scheduling;

namespace QuizBot.Persistence;

/// <summary>
/// Registered as scoped, so one instance is one unit of work: per tick in the scheduler, per update in the
/// update handler. Background services create those scopes themselves via <c>IServiceScopeFactory</c>.
/// </summary>
public sealed class QuizDbContext(DbContextOptions<QuizDbContext> options) : DbContext(options)
{
    public DbSet<StoredQuestion> Questions => Set<StoredQuestion>();
    public DbSet<ScheduledSlot> ScheduledSlots => Set<ScheduledSlot>();
    public DbSet<AskedQuestion> AskedQuestions => Set<AskedQuestion>();
    public DbSet<BotStateEntry> BotState => Set<BotStateEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date/time type, and EF Core refuses to translate comparisons or ORDER BY on
        // DateTimeOffset there. Storing UTC ticks as INTEGER keeps "due slots" and "last 7 days" queries in SQL.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();

        // Enums as text keep the database readable with the sqlite3 CLI.
        configurationBuilder.Properties<SlotStatus>().HaveConversion<string>();
        configurationBuilder.Properties<QuestionSource>().HaveConversion<string>();
        configurationBuilder.Properties<Grade>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredQuestion>(e =>
        {
            e.ToTable("Questions");
            e.HasKey(q => q.Id);
        });

        modelBuilder.Entity<ScheduledSlot>(e =>
        {
            e.HasIndex(s => new { s.Status, s.FireAtUtc });
            e.HasOne(s => s.AskedQuestion)
                .WithMany()
                .HasForeignKey(s => s.AskedQuestionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AskedQuestion>(e =>
        {
            e.HasIndex(a => new { a.QuestionId, a.AskedAtUtc });
            e.HasOne(a => a.Question)
                .WithMany()
                .HasForeignKey(a => a.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BotStateEntry>(e =>
        {
            e.ToTable("BotState");
            e.HasKey(s => s.Key);
        });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.UtcTicks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
}
