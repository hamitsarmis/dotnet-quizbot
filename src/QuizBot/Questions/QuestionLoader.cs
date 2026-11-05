using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;

namespace QuizBot.Questions;

/// <summary>
/// Reads and validates the question bank from <c>questions/*.json</c> and upserts it into the database.
/// </summary>
public sealed partial class QuestionLoader(QuizDbContext db, TimeProvider timeProvider)
{
    /// <summary>Leaves room for the header and follow-ups inside Telegram's 4096-character message limit.</summary>
    public const int MaxAnswerLength = 3500;

    public static readonly IReadOnlySet<string> Difficulties = new HashSet<string>(StringComparer.Ordinal) { "core", "senior", "deep" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex TopicSlug();

    /// <summary>
    /// Reads every <c>*.json</c> file in <paramref name="directory"/> (ordered by name) and validates the whole bank.
    /// </summary>
    /// <exception cref="QuestionValidationException">The directory is missing or any file is invalid.</exception>
    public static IReadOnlyList<QuestionDefinition> ReadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new QuestionValidationException([$"Questions directory '{directory}' does not exist."]);
        }

        var files = Directory.GetFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => (Source: Path.GetFileName(path), Json: File.ReadAllText(path)));

        var questions = Parse(files);
        if (questions.Count == 0)
        {
            // An empty or wrongly mounted directory would otherwise deactivate the whole bank.
            throw new QuestionValidationException([$"No questions found in '{directory}'."]);
        }

        return questions;
    }

    /// <summary>Reads the bank from <paramref name="directory"/> and synchronises the database with it.</summary>
    public Task<QuestionSyncResult> LoadAsync(string directory, CancellationToken cancellationToken) =>
        SyncAsync(ReadDirectory(directory), cancellationToken);

    /// <summary>
    /// Upserts by id and deactivates stored questions that are no longer defined. Idempotent: running it
    /// twice with the same definitions changes nothing the second time.
    /// </summary>
    public async Task<QuestionSyncResult> SyncAsync(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var stored = await db.Questions.ToDictionaryAsync(q => q.Id, StringComparer.Ordinal, cancellationToken);
        int added = 0, updated = 0, deactivated = 0;

        foreach (var question in questions)
        {
            var followupsJson = JsonSerializer.Serialize(question.Followups);
            var tagsJson = JsonSerializer.Serialize(question.Tags);

            if (!stored.TryGetValue(question.Id, out var existing))
            {
                db.Questions.Add(new StoredQuestion
                {
                    Id = question.Id,
                    Topic = question.Topic,
                    Difficulty = question.Difficulty,
                    Question = question.Question,
                    Answer = question.Answer,
                    FollowupsJson = followupsJson,
                    TagsJson = tagsJson,
                    IsActive = true,
                    UpdatedAtUtc = now,
                });
                added++;
                continue;
            }

            var unchanged = existing.IsActive
                && existing.Topic == question.Topic
                && existing.Difficulty == question.Difficulty
                && existing.Question == question.Question
                && existing.Answer == question.Answer
                && existing.FollowupsJson == followupsJson
                && existing.TagsJson == tagsJson;
            if (unchanged)
            {
                continue;
            }

            existing.Topic = question.Topic;
            existing.Difficulty = question.Difficulty;
            existing.Question = question.Question;
            existing.Answer = question.Answer;
            existing.FollowupsJson = followupsJson;
            existing.TagsJson = tagsJson;
            existing.IsActive = true;
            existing.UpdatedAtUtc = now;
            updated++;
        }

        var definedIds = questions.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var existing in stored.Values.Where(q => q.IsActive && !definedIds.Contains(q.Id)))
        {
            existing.IsActive = false;
            existing.UpdatedAtUtc = now;
            deactivated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new QuestionSyncResult(added, updated, deactivated, questions.Count);
    }

    /// <summary>
    /// Parses and validates question files. All problems are collected and reported together so that
    /// one restart is enough to see everything wrong with an edit.
    /// </summary>
    /// <exception cref="QuestionValidationException">Any file is invalid, or ids collide across files.</exception>
    public static IReadOnlyList<QuestionDefinition> Parse(IEnumerable<(string Source, string Json)> files)
    {
        var errors = new List<string>();
        var questions = new List<QuestionDefinition>();
        var firstSourceById = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (source, json) in files)
        {
            List<QuestionFileEntry?>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<QuestionFileEntry?>>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                errors.Add($"{source}: invalid JSON ({ex.Message})");
                continue;
            }

            if (entries is null)
            {
                errors.Add($"{source}: expected a JSON array of questions.");
                continue;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var location = $"{source}[{i}]";
                if (entry is null)
                {
                    errors.Add($"{location}: entry is null.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.Id))
                {
                    location = $"{source} '{entry.Id}'";
                }

                var entryErrors = Validate(entry).Select(e => $"{location}: {e}").ToList();
                if (entryErrors.Count > 0)
                {
                    errors.AddRange(entryErrors);
                    continue;
                }

                if (firstSourceById.TryGetValue(entry.Id!, out var firstSource))
                {
                    errors.Add($"{location}: duplicate id, already defined in {firstSource}.");
                    continue;
                }

                firstSourceById[entry.Id!] = source;
                questions.Add(new QuestionDefinition(
                    entry.Id!,
                    entry.Topic!,
                    entry.Difficulty!,
                    entry.Question!,
                    entry.Answer!,
                    entry.Followups ?? [],
                    entry.Tags ?? []));
            }
        }

        if (errors.Count > 0)
        {
            throw new QuestionValidationException(errors);
        }

        return questions;
    }

    private static IEnumerable<string> Validate(QuestionFileEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            yield return "'id' is required.";
        }

        if (string.IsNullOrWhiteSpace(entry.Topic))
        {
            yield return "'topic' is required.";
        }
        else if (!TopicSlug().IsMatch(entry.Topic))
        {
            yield return $"'topic' must be a lowercase slug, got '{entry.Topic}'.";
        }

        if (entry.Difficulty is null || !Difficulties.Contains(entry.Difficulty))
        {
            yield return $"'difficulty' must be one of {string.Join(", ", Difficulties)}, got '{entry.Difficulty}'.";
        }

        if (string.IsNullOrWhiteSpace(entry.Question))
        {
            yield return "'question' is required.";
        }

        if (string.IsNullOrWhiteSpace(entry.Answer))
        {
            yield return "'answer' is required.";
        }
        else if (entry.Answer.Length > MaxAnswerLength)
        {
            yield return $"'answer' is {entry.Answer.Length} characters, the limit is {MaxAnswerLength}.";
        }

        if (entry.Followups?.Any(string.IsNullOrWhiteSpace) == true)
        {
            yield return "'followups' must not contain empty entries.";
        }

        if (entry.Tags?.Any(string.IsNullOrWhiteSpace) == true)
        {
            yield return "'tags' must not contain empty entries.";
        }
    }

    /// <summary>Lenient shape of a file entry; <see cref="Validate"/> turns it into a <see cref="QuestionDefinition"/>.</summary>
    private sealed class QuestionFileEntry
    {
        public string? Id { get; init; }
        public string? Topic { get; init; }
        public string? Difficulty { get; init; }
        public string? Question { get; init; }
        public string? Answer { get; init; }
        public List<string>? Followups { get; init; }
        public List<string>? Tags { get; init; }
    }
}

public sealed record QuestionSyncResult(int Added, int Updated, int Deactivated, int Active);

public sealed class QuestionValidationException(IReadOnlyList<string> errors)
    : Exception("Question files are invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
