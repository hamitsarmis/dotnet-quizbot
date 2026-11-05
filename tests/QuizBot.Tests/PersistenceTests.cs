using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;
using QuizBot.Scheduling;

namespace QuizBot.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void MigrationsMatchTheModel()
    {
        using var db = _database.CreateContext();

        // Fails when an entity changed without `dotnet ef migrations add`.
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [Fact]
    public async Task DueSlotQueryRunsInSqlAndRoundTripsUtc()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        await using (var db = _database.CreateContext())
        {
            db.ScheduledSlots.AddRange(
                Slot(now.AddMinutes(5), SlotStatus.Pending),
                Slot(now.AddMinutes(-1), SlotStatus.Pending),
                Slot(now.AddMinutes(-90), SlotStatus.Pending),
                Slot(now.AddMinutes(-30), SlotStatus.Sent));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = _database.CreateContext())
        {
            var due = await db.ScheduledSlots
                .Where(s => s.Status == SlotStatus.Pending && s.FireAtUtc <= now)
                .OrderBy(s => s.FireAtUtc)
                .ToListAsync(TestContext.Current.CancellationToken);

            Assert.Equal([now.AddMinutes(-90), now.AddMinutes(-1)], due.Select(s => s.FireAtUtc));
            Assert.All(due, s => Assert.Equal(TimeSpan.Zero, s.FireAtUtc.Offset));
        }
    }

    [Fact]
    public async Task NonUtcOffsetsAreStoredAsTheSameInstant()
    {
        var local = new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.FromHours(3));
        await using (var db = _database.CreateContext())
        {
            db.ScheduledSlots.Add(Slot(local, SlotStatus.Pending));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = _database.CreateContext())
        {
            var slot = await db.ScheduledSlots.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(local, slot.FireAtUtc);
            Assert.Equal(12, slot.FireAtUtc.Hour);
        }
    }

    private static ScheduledSlot Slot(DateTimeOffset fireAtUtc, SlotStatus status) =>
        new() { LocalDate = DateOnly.FromDateTime(fireAtUtc.UtcDateTime), FireAtUtc = fireAtUtc, Status = status };
}
