using Microsoft.Extensions.Logging;

namespace XUnitTest.RateLimit;

internal sealed class CapturingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public CapturingLogger Logger { get; } = new();

    public ILogger CreateLogger(string categoryName) => Logger;

    public void Dispose()
    {
    }
}
