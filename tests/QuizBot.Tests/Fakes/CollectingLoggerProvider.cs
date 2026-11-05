using System.Collections.Concurrent;

namespace QuizBot.Tests.Fakes;

public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>Captures every log line so tests can assert on warnings and on what never gets logged.</summary>
public sealed class CollectingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
    }
}
