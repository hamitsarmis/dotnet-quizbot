namespace QuizBot.Persistence;

/// <summary>Typed access to <see cref="QuizDbContext.BotState"/>.</summary>
public sealed class BotStateStore(QuizDbContext db)
{
    private const string PausedKey = "paused";

    public async Task<bool> IsPausedAsync(CancellationToken cancellationToken) =>
        (await db.BotState.FindAsync([PausedKey], cancellationToken))?.Value == "true";

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        var value = paused ? "true" : "false";
        var entry = await db.BotState.FindAsync([PausedKey], cancellationToken);
        if (entry is null)
        {
            db.BotState.Add(new BotStateEntry { Key = PausedKey, Value = value });
        }
        else
        {
            entry.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
