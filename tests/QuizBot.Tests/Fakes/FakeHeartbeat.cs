using QuizBot.Scheduling;

namespace QuizBot.Tests.Fakes;

/// <summary>Counts scheduler ticks so tests can wait for one to finish instead of sleeping.</summary>
public sealed class FakeHeartbeat : IHeartbeat
{
    private readonly SemaphoreSlim _beats = new(0);

    public void Beat() => _beats.Release();

    public async Task WaitForBeatAsync()
    {
        if (!await _beats.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The scheduler did not complete a tick (check the logs for a tick failure).");
        }
    }
}
