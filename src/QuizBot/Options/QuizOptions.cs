using System.ComponentModel.DataAnnotations;
using QuizBot.Scheduling;

namespace QuizBot.Options;

/// <summary>
/// Bound from the <c>Quiz</c> section, i.e. environment variables such as <c>Quiz__WindowStart=09:00</c>.
/// Validated with <c>ValidateOnStart</c>, so a bad value stops the container at boot with a clear message
/// instead of surfacing at 09:00 when the first question should have been sent.
/// </summary>
public sealed class QuizOptions : IValidatableObject
{
    public const string SectionName = "Quiz";

    [Required]
    public string TimeZone { get; set; } = "Europe/Istanbul";

    public TimeOnly WindowStart { get; set; } = new(9, 0);

    public TimeOnly WindowEnd { get; set; } = new(22, 0);

    [Range(1, 48)]
    public int QuestionsPerDay { get; set; } = 4;

    [Range(0, 1440)]
    public int MinGapMinutes { get; set; } = 60;

    [Required]
    public string QuestionsDir { get; set; } = "/questions";

    [Required]
    public string DbPath { get; set; } = "/data/quizbot.db";

    /// <summary>Touched by the scheduler on every successful tick; the Docker HEALTHCHECK reads its age.</summary>
    [Required]
    public string HeartbeatPath { get; set; } = "/tmp/quizbot.heartbeat";

    public ScheduleWindow ToScheduleWindow() =>
        new(WindowStart, WindowEnd, QuestionsPerDay, TimeSpan.FromMinutes(MinGapMinutes));

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ToScheduleWindow().Validate() is { } problem)
        {
            yield return new ValidationResult(problem, [nameof(WindowStart), nameof(WindowEnd), nameof(QuestionsPerDay), nameof(MinGapMinutes)]);
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
        {
            yield return new ValidationResult(
                $"Time zone '{TimeZone}' could not be resolved. On Linux this usually means the tzdata package is missing from the image.",
                [nameof(TimeZone)]);
        }
    }
}
