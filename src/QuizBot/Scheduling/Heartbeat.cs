using Microsoft.Extensions.Options;
using QuizBot.Options;

namespace QuizBot.Scheduling;

/// <summary>Liveness signal: the scheduler beats after every successful tick.</summary>
public interface IHeartbeat
{
    void Beat();
}

/// <summary>
/// Writes the current time to a file. The Docker HEALTHCHECK only looks at the file's age, so a hung or
/// failing ticker turns the container unhealthy without the bot needing an HTTP endpoint.
/// </summary>
public sealed class FileHeartbeat(IOptions<QuizOptions> options, TimeProvider timeProvider) : IHeartbeat
{
    public void Beat() => File.WriteAllText(options.Value.HeartbeatPath, timeProvider.GetUtcNow().ToString("O"));
}
