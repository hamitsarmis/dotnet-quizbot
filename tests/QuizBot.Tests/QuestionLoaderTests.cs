using QuizBot.Questions;

namespace QuizBot.Tests;

public sealed class QuestionLoaderTests
{
    private const string ValidQuestion = """
        { "id": "gc-001", "topic": "gc", "difficulty": "core", "question": "Q?", "answer": "A.",
          "followups": ["F?"], "tags": ["t"] }
        """;

    [Fact]
    public void ParsesAValidFile()
    {
        var questions = QuestionLoader.Parse([("a.json", $"[{ValidQuestion}]")]);

        var question = Assert.Single(questions);
        Assert.Equal(("gc-001", "gc", "core", "Q?", "A."), (question.Id, question.Topic, question.Difficulty, question.Question, question.Answer));
        Assert.Equal(["F?"], question.Followups);
        Assert.Equal(["t"], question.Tags);
    }

    [Fact]
    public void MissingFollowupsAndTagsDefaultToEmpty()
    {
        const string json = """[{ "id": "x-1", "topic": "x", "difficulty": "deep", "question": "Q", "answer": "A" }]""";

        var question = Assert.Single(QuestionLoader.Parse([("a.json", json)]));

        Assert.Empty(question.Followups);
        Assert.Empty(question.Tags);
    }

    [Fact]
    public void DuplicateIdAcrossFilesIsRejectedAndNamesBothFiles()
    {
        var ex = Assert.Throws<QuestionValidationException>(() =>
            QuestionLoader.Parse([("a.json", $"[{ValidQuestion}]"), ("b.json", $"[{ValidQuestion}]")]));

        var error = Assert.Single(ex.Errors);
        Assert.Contains("b.json", error);
        Assert.Contains("a.json", error);
        Assert.Contains("duplicate", error);
    }

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        const string json = """
            [
              { "id": "", "topic": "Not A Slug", "difficulty": "easy", "question": " ", "answer": "" },
              { "id": "ok-1", "topic": "ok", "difficulty": "core", "question": "Q", "answer": "A", "followups": [""] }
            ]
            """;

        var ex = Assert.Throws<QuestionValidationException>(() =>
            QuestionLoader.Parse([("a.json", json), ("broken.json", "{ not json")]));

        Assert.Contains(ex.Errors, e => e.Contains("'id' is required"));
        Assert.Contains(ex.Errors, e => e.Contains("lowercase slug"));
        Assert.Contains(ex.Errors, e => e.Contains("'difficulty'"));
        Assert.Contains(ex.Errors, e => e.Contains("'question' is required"));
        Assert.Contains(ex.Errors, e => e.Contains("'answer' is required"));
        Assert.Contains(ex.Errors, e => e.Contains("'ok-1'") && e.Contains("followups"));
        Assert.Contains(ex.Errors, e => e.StartsWith("broken.json: invalid JSON"));
    }

    [Fact]
    public void AnswerOverTheLimitIsRejected()
    {
        var answer = new string('a', QuestionLoader.MaxAnswerLength + 1);
        var json = $$"""[{ "id": "x-1", "topic": "x", "difficulty": "core", "question": "Q", "answer": "{{answer}}" }]""";

        var ex = Assert.Throws<QuestionValidationException>(() => QuestionLoader.Parse([("a.json", json)]));

        Assert.Contains("limit", Assert.Single(ex.Errors));
    }

    [Fact]
    public void MissingDirectoryIsAClearError()
    {
        var ex = Assert.Throws<QuestionValidationException>(() =>
            QuestionLoader.ReadDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        Assert.Contains("does not exist", ex.Message);
    }
}
