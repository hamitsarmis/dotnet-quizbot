using System.Text;
using QuizBot.Persistence;

namespace QuizBot.Questions;

/// <summary>
/// Renders questions for Telegram's HTML parse mode: everything is HTML-escaped, and text between
/// backticks becomes <c>&lt;code&gt;</c>. Pure, so the rendering rules are unit-tested directly.
/// </summary>
public static class QuestionFormatter
{
    public static string FormatQuestion(StoredQuestion question) =>
        $"""
        ❓ <b>{Escape(question.Topic)}</b> · {Escape(question.Difficulty)} · <code>{Escape(question.Id)}</code>

        {ToTelegramHtml(question.Question)}

        <i>/answer to reveal · /skip to pass</i>
        """;

    public static string FormatReveal(StoredQuestion question)
    {
        var html = new StringBuilder()
            .Append("💡 <b>Model answer</b> · <code>").Append(Escape(question.Id)).Append("</code>\n\n")
            .Append(ToTelegramHtml(question.Answer));

        var followups = question.GetFollowups();
        if (followups.Count > 0)
        {
            html.Append("\n\n<b>Follow-ups</b>");
            foreach (var followup in followups)
            {
                html.Append("\n• ").Append(ToTelegramHtml(followup));
            }
        }

        return html.ToString();
    }

    /// <summary>
    /// Escapes <paramref name="text"/> and turns <c>`code`</c> spans into <c>&lt;code&gt;</c>. An unmatched
    /// trailing backtick is kept literally rather than opening a tag that is never closed.
    /// </summary>
    public static string ToTelegramHtml(string text)
    {
        var parts = text.Split('`');
        var html = new StringBuilder(text.Length + 32);
        for (var i = 0; i < parts.Length; i++)
        {
            var isCode = i % 2 == 1;
            var isUnmatchedTail = isCode && i == parts.Length - 1;
            if (isUnmatchedTail)
            {
                html.Append('`').Append(Escape(parts[i]));
            }
            else if (isCode)
            {
                html.Append("<code>").Append(Escape(parts[i])).Append("</code>");
            }
            else
            {
                html.Append(Escape(parts[i]));
            }
        }

        return html.ToString();
    }

    /// <summary>Telegram's HTML mode only requires these three characters to be escaped.</summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
