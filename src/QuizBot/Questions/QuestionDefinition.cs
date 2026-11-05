namespace QuizBot.Questions;

/// <summary>A validated question as defined in a <c>questions/*.json</c> file.</summary>
public sealed record QuestionDefinition(
    string Id,
    string Topic,
    string Difficulty,
    string Question,
    string Answer,
    IReadOnlyList<string> Followups,
    IReadOnlyList<string> Tags);
