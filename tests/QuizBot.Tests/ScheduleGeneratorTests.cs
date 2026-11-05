using QuizBot.Scheduling;

namespace QuizBot.Tests;

public sealed class ScheduleGeneratorTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly DateOnly Day = new(2026, 10, 23);
    private static readonly ScheduleWindow DefaultWindow = new(new TimeOnly(9, 0), new TimeOnly(22, 0), 4, TimeSpan.FromMinutes(60));

    /// <summary>Midnight local time: the whole window is still ahead.</summary>
    private static readonly DateTimeOffset StartOfDay = new(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(0, 50)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratesExactlyNSlotsInsideTheWindow(int seed)
    {
        var slots = Generate(seed, DefaultWindow, StartOfDay);

        Assert.Equal(4, slots.Count);
        Assert.All(slots, slot =>
        {
            var local = TimeZoneInfo.ConvertTime(slot.FireAtUtc, Istanbul);
            Assert.Equal(Day, DateOnly.FromDateTime(local.DateTime));
            Assert.InRange(TimeOnly.FromDateTime(local.DateTime), DefaultWindow.Start, DefaultWindow.End);
            Assert.Equal(SlotStatus.Pending, slot.Status);
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RespectsTheMinimumGapAndStaysOrdered(int seed)
    {
        var slots = Generate(seed, DefaultWindow, StartOfDay);

        AssertGaps(slots, DefaultWindow.MinGap);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GapLargerThanABucketStillFitsTheWindow(int seed)
    {
        // 3-hour window, 4 slots: buckets are 45 minutes but the gap is 55, so the forward
        // nudge can overshoot the window end and the backward pass has to pull slots in.
        var tight = new ScheduleWindow(new TimeOnly(9, 0), new TimeOnly(12, 0), 4, TimeSpan.FromMinutes(55));

        var slots = Generate(seed, tight, StartOfDay);

        AssertGaps(slots, tight.MinGap);
        Assert.All(slots, slot =>
            Assert.InRange(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot.FireAtUtc, Istanbul).DateTime), tight.Start, tight.End));
    }

    [Fact]
    public void IsDeterministicForASeededRandom()
    {
        var first = Generate(42, DefaultWindow, StartOfDay);
        var second = Generate(42, DefaultWindow, StartOfDay);
        var other = Generate(43, DefaultWindow, StartOfDay);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void SlotsBeforeNowAreSkippedWhenGeneratedMidDay()
    {
        // 15:30 Istanbul (UTC+3).
        var now = new DateTimeOffset(2026, 10, 23, 12, 30, 0, TimeSpan.Zero);

        var slots = Generate(7, DefaultWindow, now);

        Assert.Equal(4, slots.Count);
        Assert.All(slots, slot => Assert.Equal(slot.FireAtUtc <= now ? SlotStatus.Skipped : SlotStatus.Pending, slot.Status));
        Assert.Contains(slots, s => s.Status == SlotStatus.Skipped);
        Assert.Contains(slots, s => s.Status == SlotStatus.Pending);
    }

    [Fact]
    public void AfterTheWindowEveryGeneratedSlotIsSkipped()
    {
        var lateEvening = new DateTimeOffset(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);

        var slots = Generate(7, DefaultWindow, lateEvening);

        Assert.All(slots, slot => Assert.Equal(SlotStatus.Skipped, slot.Status));
    }

    [Fact]
    public void WindowEndBeforeStartIsAClearError()
    {
        var inverted = DefaultWindow with { Start = new TimeOnly(22, 0), End = new TimeOnly(9, 0) };

        var ex = Assert.Throws<ArgumentException>(() => Generate(1, inverted, StartOfDay));

        Assert.Contains("must be after window start", ex.Message);
    }

    [Fact]
    public void GapsThatCannotFitAreAClearError()
    {
        var impossible = DefaultWindow with { SlotsPerDay = 10, MinGap = TimeSpan.FromHours(2) };

        var ex = Assert.Throws<ArgumentException>(() => Generate(1, impossible, StartOfDay));

        Assert.Contains("do not fit", ex.Message);
    }

    [Fact]
    public void SingleSlotDaysWork()
    {
        var slots = Generate(3, DefaultWindow with { SlotsPerDay = 1 }, StartOfDay);

        Assert.Single(slots);
    }

    [Fact]
    public void ToUtcStepsOverADaylightSavingGap()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

        // 02:30 does not exist on 2026-03-29 in Berlin: clocks jump from 02:00 to 03:00 (UTC+2).
        var utc = ScheduleGenerator.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0), berlin);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), utc);
    }

    private static IReadOnlyList<PlannedSlot> Generate(int seed, ScheduleWindow window, DateTimeOffset now) =>
        new ScheduleGenerator(new Random(seed)).Generate(Day, window, Istanbul, now);

    private static void AssertGaps(IReadOnlyList<PlannedSlot> slots, TimeSpan minGap)
    {
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.True(slots[i].FireAtUtc - slots[i - 1].FireAtUtc >= minGap,
                $"Slots {i - 1} and {i} are {(slots[i].FireAtUtc - slots[i - 1].FireAtUtc).TotalMinutes:0.#} minutes apart.");
        }
    }
}
