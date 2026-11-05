using System.Text.Json;
using QuizBot.Persistence;
using QuizBot.Questions;

namespace QuizBot.Tests;

public sealed class QuestionFormatterTests
{
    [Theory]
    [InlineData("a < b && c > d", "a &lt; b &amp;&amp; c &gt; d")]
    [InlineData("use `List<T>` here", "use <code>List&lt;T&gt;</code> here")]
    [InlineData("`a` and `b`", "<code>a</code> and <code>b</code>")]
    [InlineData("an unmatched `tick & more", "an unmatched `tick &amp; more")]
    [InlineData("``", "<code></code>")]
    [InlineData("<b>not bold</b>", "&lt;b&gt;not bold&lt;/b&gt;")]
    public void ToTelegramHtmlEscapesAndRendersCode(string text, string expected)
    {
        Assert.Equal(expected, QuestionFormatter.ToTelegramHtml(text));
    }

    [Fact]
    public void QuestionShowsTopicDifficultyIdAndHowToContinue()
    {
        var html = QuestionFormatter.FormatQuestion(Stored("gc-001", question: "Why `GC.Collect()`?"));

        Assert.StartsWith("❓ <b>gc</b> · core · <code>gc-001</code>", html);
        Assert.Contains("Why <code>GC.Collect()</code>?", html);
        Assert.Contains("/answer", html);
    }

    [Fact]
    public void RevealListsFollowups()
    {
        var html = QuestionFormatter.FormatReveal(Stored("gc-001", answer: "Because `x`.", followups: ["One?", "Two & three?"]));

        Assert.Contains("Because <code>x</code>.", html);
        Assert.Contains("<b>Follow-ups</b>\n• One?\n• Two &amp; three?", html);
    }

    [Fact]
    public void RevealWithoutFollowupsHasNoFollowupSection()
    {
        Assert.DoesNotContain("Follow-ups", QuestionFormatter.FormatReveal(Stored("gc-001", followups: [])));
    }

    [Fact]
    public void EveryQuestionInTheBankRendersBalancedTags()
    {
        var bank = QuestionLoader.ReadDirectory(Path.Combine(AppContext.BaseDirectory, "questions"));

        Assert.All(bank, definition =>
        {
            var stored = Stored(definition.Id, definition.Question, definition.Answer, definition.Followups);
            foreach (var html in new[] { QuestionFormatter.FormatQuestion(stored), QuestionFormatter.FormatReveal(stored) })
            {
                Assert.Equal(Count(html, "<code>"), Count(html, "</code>"));
                Assert.Equal(Count(html, "<b>"), Count(html, "</b>"));
            }
        });
    }

    private static int Count(string text, string value) => (text.Length - text.Replace(value, "").Length) / value.Length;

    private static StoredQuestion Stored(string id, string question = "Q?", string answer = "A.", IReadOnlyList<string>? followups = null) => new()
    {
        Id = id,
        Topic = "gc",
        Difficulty = "core",
        Question = question,
        Answer = answer,
        FollowupsJson = JsonSerializer.Serialize(followups ?? ["F?"]),
        TagsJson = "[]",
        IsActive = true,
    };
}
