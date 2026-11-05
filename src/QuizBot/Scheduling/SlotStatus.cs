namespace QuizBot.Scheduling;

public enum SlotStatus
{
    Pending,
    Sent,
    Skipped,
    Failed,
}

/// <summary>A slot produced by <see cref="ScheduleGenerator"/>, before it is persisted.</summary>
public sealed record PlannedSlot(DateTimeOffset FireAtUtc, SlotStatus Status);
