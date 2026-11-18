using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace MeetingAgent.Infrastructure;

public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly int _maxEntries;

    public InMemoryLogSink(int maxEntries = 400)
    {
        _maxEntries = Math.Max(50, maxEntries);
    }

    public event EventHandler<string>? LogAdded;

    public IReadOnlyCollection<string> Entries => _entries.ToArray();

    public void Emit(LogEvent logEvent)
    {
        var message = $"[{logEvent.Timestamp:HH:mm:ss}] {logEvent.Level}: {logEvent.RenderMessage()}";
        _entries.Enqueue(message);
        while (_entries.Count > _maxEntries && _entries.TryDequeue(out _))
        {
        }

        LogAdded?.Invoke(this, message);
    }
}
