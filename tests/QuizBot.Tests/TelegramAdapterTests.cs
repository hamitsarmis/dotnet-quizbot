using QuizBot.Telegram;

namespace QuizBot.Tests;

/// <summary>
/// The parts of the Telegram adapter that need no network and no <c>Telegram.Bot</c> types: splitting long
/// messages and redacting the token from error messages.
/// </summary>
public sealed class TelegramAdapterTests
{
    [Fact]
    public void ShortMessagesAreNotSplit()
    {
        Assert.Equal(["hello"], MessageSplitter.Split("hello"));
    }

    [Fact]
    public void LongMessagesSplitAtParagraphBoundariesWithinTheLimit()
    {
        var paragraphs = Enumerable.Range(0, 30).Select(i => $"<b>{i}</b> " + new string('x', 300)).ToList();
        var html = string.Join("\n\n", paragraphs);

        var parts = MessageSplitter.Split(html);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, MessageSplitter.MaxLength));
        Assert.Equal(html, string.Join("\n\n", parts));
        Assert.All(parts, p => Assert.Equal(p.Split("<b>").Length, p.Split("</b>").Length));
    }

    [Fact]
    public void AnOversizedParagraphIsCutAtAWordBoundary()
    {
        var html = string.Join(' ', Enumerable.Repeat("word", 2000));

        var parts = MessageSplitter.Split(html, maxLength: 1000);

        Assert.All(parts, p => Assert.InRange(p.Length, 1, 1000));
        Assert.All(parts, p => Assert.DoesNotContain("wo rd", p));
        Assert.Equal(html.Replace(" ", ""), string.Concat(parts).Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void ACutInsideACodeSpanClosesAndReopensTheTag()
    {
        // One paragraph, no blank lines, with spaces inside the code spans: some cuts must land inside one.
        var html = string.Concat(Enumerable.Repeat("see <code>Task.Run(() =&gt; Work(a, b))</code> and ", 300));

        var parts = MessageSplitter.Split(html, maxLength: 1000);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, 1000));
        Assert.All(parts, AssertTagsBalanced);
    }

    [Fact]
    public void AHardCutNeverSplitsAnEntity()
    {
        var html = string.Concat(Enumerable.Repeat("&lt;", 3000));

        // 1001 is not a multiple of the entity length, so a naive cut would land inside one.
        var parts = MessageSplitter.Split(html, maxLength: 1001);

        Assert.All(parts, p => Assert.Matches("^(&lt;)+$", p));
        Assert.Equal(html, string.Concat(parts));
    }

    [Theory]
    [InlineData("POST https://api.telegram.org/bot123456:AAH-secret_value_xyz/sendMessage failed", "POST https://api.telegram.org/bot***/sendMessage failed")]
    [InlineData("secret half AAH-secret_value_xyz alone", "secret half *** alone")]
    [InlineData("nothing sensitive", "nothing sensitive")]
    public void RedactRemovesTheTokenAndItsSecretHalf(string text, string expected)
    {
        Assert.Equal(expected, TelegramMessenger.Redact(text, "123456:AAH-secret_value_xyz"));
    }

    /// <summary>Every closing tag matches the innermost open one, and nothing is left open.</summary>
    private static void AssertTagsBalanced(string html)
    {
        var open = new Stack<string>();
        foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(html, "<(/?)(b|i|code)>"))
        {
            if (tag.Groups[1].Value == "/")
            {
                Assert.True(open.TryPop(out var innermost) && innermost == tag.Groups[2].Value, $"Unbalanced tags in: {html}");
            }
            else
            {
                open.Push(tag.Groups[2].Value);
            }
        }

        Assert.Empty(open);
    }
}
