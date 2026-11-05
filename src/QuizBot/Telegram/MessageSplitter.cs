using System.Text;
using System.Text.RegularExpressions;

namespace QuizBot.Telegram;

/// <summary>
/// Splits long HTML into Telegram-sized messages at paragraph boundaries. The formatter only emits inline tags
/// inside paragraphs, so a paragraph boundary never cuts through a tag.
/// </summary>
public static partial class MessageSplitter
{
    /// <summary>
    /// Telegram rejects messages over 4096 characters (counted after HTML parsing). Splitting the raw HTML at 4000
    /// is conservative, since tags and entities only make the raw text longer, and leaves room to append a line later.
    /// </summary>
    public const int MaxLength = 4000;

    /// <summary>Room for the closing tags appended to a cut piece: at most <c>&lt;/code&gt;&lt;/i&gt;&lt;/b&gt;</c>.</summary>
    private const int ClosingTagsReserve = 16;

    public static IReadOnlyList<string> Split(string html, int maxLength = MaxLength)
    {
        if (html.Length <= maxLength)
        {
            return [html];
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in html.Split("\n\n"))
        {
            foreach (var piece in SplitOversizedParagraph(paragraph, maxLength))
            {
                if (current.Length > 0 && current.Length + 2 + piece.Length > maxLength)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }

                current.Append(piece);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>
    /// Fallback for a single paragraph over the limit: cut at the last line break, else the last space. A cut can land
    /// inside a code span, so tags still open at the cut are closed at the end of the piece and reopened at the start
    /// of the next one; otherwise Telegram rejects both messages with "can't parse entities".
    /// </summary>
    private static IEnumerable<string> SplitOversizedParagraph(string paragraph, int maxLength)
    {
        var budget = maxLength - ClosingTagsReserve;
        while (paragraph.Length > maxLength)
        {
            var cut = FindCut(paragraph, budget);
            var piece = paragraph[..cut].TrimEnd();
            var openTags = OpenTags(piece);
            yield return piece + string.Concat(openTags.AsEnumerable().Reverse().Select(tag => $"</{tag}>"));
            paragraph = string.Concat(openTags.Select(tag => $"<{tag}>")) + paragraph[cut..].TrimStart();
        }

        yield return paragraph;
    }

    private static int FindCut(string text, int budget)
    {
        var cut = text.LastIndexOf('\n', budget);
        if (cut <= 0)
        {
            cut = text.LastIndexOf(' ', budget);
        }

        if (cut > 0)
        {
            return cut;
        }

        // No whitespace at all: a hard cut, moved back so it never splits a tag (<code>) or an entity (&amp;).
        cut = budget;
        var tagStart = text.LastIndexOf('<', cut - 1);
        if (tagStart > 0 && tagStart > text.LastIndexOf('>', cut - 1))
        {
            cut = tagStart;
        }

        var entityStart = text.LastIndexOf('&', cut - 1);
        if (entityStart > 0 && entityStart > text.LastIndexOf(';', cut - 1))
        {
            cut = entityStart;
        }

        return cut;
    }

    /// <summary>Tags opened in <paramref name="html"/> and not closed, in opening order.</summary>
    private static List<string> OpenTags(string html)
    {
        var open = new List<string>();
        foreach (Match match in TagPattern().Matches(html))
        {
            var name = match.Groups["name"].Value;
            if (match.Groups["closing"].Success)
            {
                var index = open.LastIndexOf(name);
                if (index >= 0)
                {
                    open.RemoveAt(index);
                }
            }
            else
            {
                open.Add(name);
            }
        }

        return open;
    }

    /// <summary>The only tags QuestionFormatter and the commands emit.</summary>
    [GeneratedRegex("<(?<closing>/)?(?<name>b|i|code)>")]
    private static partial Regex TagPattern();
}
