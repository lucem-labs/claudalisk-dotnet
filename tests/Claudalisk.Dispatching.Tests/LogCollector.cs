using Microsoft.Extensions.Logging;

namespace Claudalisk.Dispatching.Tests;

internal sealed class LogCollector : ILoggerProvider
{
    private readonly List<(string Category, LogLevel Level, string Message, bool HasException)> entries = [];

    public IReadOnlyList<(string Category, LogLevel Level, string Message, bool HasException)> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Collector(this, categoryName);

    public void Dispose()
    {
    }

    private void Add((string Category, LogLevel Level, string Message, bool HasException) entry)
    {
        lock (entries)
        {
            entries.Add(entry);
        }
    }

    private sealed class Collector(LogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Add((category, logLevel, formatter(state, exception), exception is not null));
        }
    }
}
