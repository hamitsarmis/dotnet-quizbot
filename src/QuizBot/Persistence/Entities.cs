using System.Text.Json;
using QuizBot.Scheduling;

namespace QuizBot.Persistence;

public enum QuestionSource
{
    Scheduled,
    Manual,
}

public enum Grade
{
    KnewIt,
    Partially,
    DidntKnow,
}

/// <summary>A question loaded from the files. Never deleted: history references it, so removal only deactivates.</summary>
public sealed class StoredQuestion
{
    public required string Id { get; set; }
    public required string Topic { get; set; }
    public required string Difficulty { get; set; }
    public required string Question { get; set; }
    public required string Answer { get; set; }
    public required string FollowupsJson { get; set; }
    public required string TagsJson { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public IReadOnlyList<string> GetFollowups() => JsonSerializer.Deserialize<List<string>>(FollowupsJson) ?? [];
}

public sealed class ScheduledSlot
{
    public int Id { get; set; }

    /// <summary>The local (configured time zone) day this slot belongs to.</summary>
    public DateOnly LocalDate { get; set; }

    public DateTimeOffset FireAtUtc { get; set; }
    public SlotStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public int? AskedQuestionId { get; set; }
    public AskedQuestion? AskedQuestion { get; set; }
}

public sealed class AskedQuestion
{
    public int Id { get; set; }
    public required string QuestionId { get; set; }
    public StoredQuestion Question { get; set; } = null!;
    public DateTimeOffset AskedAtUtc { get; set; }
    public QuestionSource Source { get; set; }
    public long TelegramMessageId { get; set; }
    public DateTimeOffset? RevealedAtUtc { get; set; }
    public Grade? Grade { get; set; }
    public DateTimeOffset? GradedAtUtc { get; set; }
    public DateTimeOffset? SkippedAtUtc { get; set; }
}

/// <summary>Small key/value store for runtime flags such as <c>paused</c>.</summary>
public sealed class BotStateEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
