using System.Threading.Channels;
using QuizBot.Telegram;

namespace QuizBot.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IQuizMessenger"/>: records what the bot sends and lets tests feed updates.
/// Thread-safe because background services call it from the thread pool while tests read it.
/// </summary>
public sealed class FakeMessenger : IQuizMessenger
{
    private readonly Lock _lock = new();
    private readonly Channel<IReadOnlyList<IncomingUpdate>> _updates = Channel.CreateUnbounded<IReadOnlyList<IncomingUpdate>>();
    private readonly List<(long MessageId, OutgoingMessage Message)> _sent = [];
    private readonly List<(long MessageId, string Html)> _edits = [];
    private readonly List<(string CallbackId, string? Text)> _callbackAnswers = [];
    private readonly List<long> _polledOffsets = [];
    private long _lastMessageId = 1000;
    private int _failNextSends;
    private int _failNextCallbackAnswers;

    public IReadOnlyList<(long MessageId, OutgoingMessage Message)> Sent { get { lock (_lock) { return [.. _sent]; } } }

    public IReadOnlyList<(long MessageId, string Html)> Edits { get { lock (_lock) { return [.. _edits]; } } }

    public IReadOnlyList<(string CallbackId, string? Text)> CallbackAnswers { get { lock (_lock) { return [.. _callbackAnswers]; } } }

    public IReadOnlyList<long> PolledOffsets { get { lock (_lock) { return [.. _polledOffsets]; } } }

    /// <summary>The next <paramref name="count"/> sends throw <see cref="QuizMessengerException"/>.</summary>
    public void FailNextSends(int count)
    {
        lock (_lock)
        {
            _failNextSends = count;
        }
    }

    /// <summary>The next <paramref name="count"/> callback acknowledgements throw <see cref="QuizMessengerException"/>.</summary>
    public void FailNextCallbackAnswers(int count)
    {
        lock (_lock)
        {
            _failNextCallbackAnswers = count;
        }
    }

    public void EnqueueUpdates(params IncomingUpdate[] updates) => _updates.Writer.TryWrite(updates);

    public async Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _polledOffsets.Add(offset);
        }

        return await _updates.Reader.ReadAsync(cancellationToken);
    }

    public Task<long> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_failNextSends > 0)
            {
                _failNextSends--;
                throw new QuizMessengerException("Simulated send failure.");
            }

            _sent.Add((++_lastMessageId, message));
            return Task.FromResult(_lastMessageId);
        }
    }

    public Task EditAsync(long messageId, string html, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _edits.Add((messageId, html));
        }

        return Task.CompletedTask;
    }

    public Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_failNextCallbackAnswers > 0)
            {
                _failNextCallbackAnswers--;
                throw new QuizMessengerException("Simulated stale callback query.");
            }

            _callbackAnswers.Add((callbackId, text));
        }

        return Task.CompletedTask;
    }
}
