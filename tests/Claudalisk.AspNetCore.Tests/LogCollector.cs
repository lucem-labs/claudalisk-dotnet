using Microsoft.Extensions.Logging;

namespace Claudalisk.AspNetCore.Tests;

internal sealed class LogCollector : ILoggerProvider
{
    public List<(string Category, LogLevel Level, bool HasException)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Collector(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Collector(LogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add((category, logLevel, exception is not null));
            }
        }
    }
}
