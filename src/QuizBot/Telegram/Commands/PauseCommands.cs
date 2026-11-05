using QuizBot.Persistence;

namespace QuizBot.Telegram.Commands;

/// <summary><c>/pause</c>: due slots are marked skipped instead of sent. <c>/ask</c> keeps working.</summary>
public sealed class PauseCommand(BotStateStore state, IQuizMessenger messenger) : IBotCommand
{
    public string Name => "pause";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        await state.SetPausedAsync(true, cancellationToken);
        await messenger.SendAsync(
            new OutgoingMessage("⏸ Scheduled questions paused. /ask still works; /resume restarts the schedule."),
            cancellationToken);
    }
}

/// <summary><c>/resume</c>: scheduled questions start again from the next slot. Skipped slots are not caught up.</summary>
public sealed class ResumeCommand(BotStateStore state, IQuizMessenger messenger) : IBotCommand
{
    public string Name => "resume";

    public async Task ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        await state.SetPausedAsync(false, cancellationToken);
        await messenger.SendAsync(new OutgoingMessage("▶️ Scheduled questions resumed."), cancellationToken);
    }
}
