using System.ComponentModel.DataAnnotations;

namespace QuizBot.Options;

/// <summary>
/// Bound from the <c>Telegram</c> section (<c>Telegram__BotToken</c>, <c>Telegram__AllowedChatId</c>).
/// Validation messages deliberately never echo the token value.
/// </summary>
/// <remarks>
/// Both rules are attributes rather than <see cref="IValidatableObject"/>: the validator only runs
/// <c>IValidatableObject.Validate</c> when every attribute passed, so a missing token would hide a missing chat id
/// until the next restart.
/// </remarks>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    [Required(ErrorMessage = "Telegram:BotToken is required (set Telegram__BotToken).")]
    [RegularExpression(@"^\d+:[A-Za-z0-9_-]+$", ErrorMessage = "Telegram:BotToken does not look like a bot token ('<digits>:<secret>').")]
    public string BotToken { get; set; } = "";

    /// <summary>The only chat the bot talks to: your private chat with it, whose id is your positive user id.</summary>
    [Range(typeof(long), "1", "9223372036854775807", ErrorMessage = "Telegram:AllowedChatId is required (set Telegram__AllowedChatId to your user id).")]
    public long AllowedChatId { get; set; }

    public override string ToString() => $"TelegramOptions {{ AllowedChatId = {AllowedChatId}, BotToken = *** }}";
}
