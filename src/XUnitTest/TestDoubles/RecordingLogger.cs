using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace XUnitTest.TestDoubles;

internal sealed record LogEntry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception);

/// <summary>Captures every log call, including its structured state, so tests can assert what is (not) logged.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), values.ToList(), exception));
    }

    public IReadOnlyList<LogEntry> At(LogLevel level) => Entries.Where(e => e.Level == level).ToList();

    /// <summary>Every rendered message, structured value and exception message, for "never logged" checks.</summary>
    public string AllText() => string.Join("\n", Entries.Select(e =>
        e.Message + "|" + string.Join("|", e.State.Select(kv => $"{kv.Key}={kv.Value}")) + "|" + e.Exception?.Message));
}
