using System.Diagnostics;

namespace QuizBot.Tests;

/// <summary>Waits for a condition set by a background loop. Real time here only bounds the wait; no logic depends on it.</summary>
public static class Eventually
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException($"Timed out waiting until {description}.");
            }

            await Task.Delay(10);
        }
    }
}
