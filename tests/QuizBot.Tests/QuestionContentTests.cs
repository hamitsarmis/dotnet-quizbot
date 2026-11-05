using System.Text.Json;
using QuizBot.Questions;

namespace QuizBot.Tests;

/// <summary>
/// Guards the real question bank in <c>questions/*.json</c>. These tests read the files with
/// <see cref="JsonDocument"/> rather than through <see cref="QuestionLoader"/>, so a loader bug cannot hide a content bug.
/// </summary>
public sealed class QuestionContentTests
{
    private static readonly string QuestionsDir = Path.Combine(AppContext.BaseDirectory, "questions");

    /// <summary>Topics the bank is expected to cover; losing one entirely is almost certainly an editing mistake.</summary>
    private static readonly string[] ExpectedTopics =
        ["async", "di", "gc", "aspnetcore", "data", "csharp", "testing", "distributed", "design", "azure"];

    private static readonly string[] RequiredFields = ["id", "topic", "difficulty", "question", "answer"];

    public static TheoryData<string> QuestionFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(QuestionsDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Fact]
    public void QuestionFilesAreCopiedToTheTestOutput()
    {
        Assert.NotEmpty(QuestionFiles());
    }

    [Theory]
    [MemberData(nameof(QuestionFiles))]
    public void EveryFileParsesAsAnArrayOfObjects(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(QuestionsDir, file)));

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.NotEqual(0, document.RootElement.GetArrayLength());
        Assert.All(document.RootElement.EnumerateArray(), e => Assert.Equal(JsonValueKind.Object, e.ValueKind));
    }

    [Fact]
    public void IdsAreUniqueAcrossFiles()
    {
        var duplicates = AllEntries()
            .GroupBy(e => e.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} in {string.Join(", ", g.Select(e => e.File))}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void RequiredFieldsAreNonEmpty()
    {
        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(QuestionsDir, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                foreach (var field in RequiredFields)
                {
                    if (!element.TryGetProperty(field, out var value)
                        || value.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        problems.Add($"{Path.GetFileName(path)}[{index}].{field}");
                    }
                }

                index++;
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void DifficultiesAndTopicsUseTheAllowedValues()
    {
        var entries = AllEntries();

        Assert.All(entries, e => Assert.Contains(e.Difficulty, QuestionLoader.Difficulties));
        Assert.All(entries, e => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", e.Topic));
    }

    [Fact]
    public void AnswersFitInOneTelegramMessage()
    {
        var tooLong = AllEntries()
            .Where(e => e.Answer.Length > QuestionLoader.MaxAnswerLength)
            .Select(e => $"{e.Id} ({e.Answer.Length} chars)")
            .ToList();

        Assert.Empty(tooLong);
    }

    [Fact]
    public void EveryExpectedTopicHasAtLeastOneQuestion()
    {
        var topics = AllEntries().Select(e => e.Topic).ToHashSet();

        Assert.All(ExpectedTopics, topic => Assert.Contains(topic, topics));
    }

    [Fact]
    public void LoaderAcceptsTheWholeBank()
    {
        var questions = QuestionLoader.ReadDirectory(QuestionsDir);

        Assert.Equal(AllEntries().Count, questions.Count);
    }

    private sealed record Entry(string File, string Id, string Topic, string Difficulty, string Answer);

    private static List<Entry> AllEntries()
    {
        var entries = new List<Entry>();
        foreach (var path in Directory.GetFiles(QuestionsDir, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var element in document.RootElement.EnumerateArray())
            {
                entries.Add(new Entry(
                    Path.GetFileName(path),
                    Text(element, "id"),
                    Text(element, "topic"),
                    Text(element, "difficulty"),
                    Text(element, "answer")));
            }
        }

        return entries;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
