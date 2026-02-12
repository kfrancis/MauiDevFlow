using Microsoft.Extensions.Logging;

namespace MauiDevFlow.Logging;

/// <summary>
/// ILoggerProvider that writes log entries to rotating JSONL files.
/// Owns the shared ReaderWriterLockSlim used to coordinate writer drain and readers.
/// </summary>
public class FileLogProvider : ILoggerProvider
{
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly FileLogWriter _writer;
    private readonly FileLogReader _reader;

    public FileLogReader Reader => _reader;
    public FileLogWriter Writer => _writer;

    public FileLogProvider(string logDir, long maxFileSizeBytes = 1_048_576, int maxFiles = 5)
    {
        _writer = new FileLogWriter(logDir, _rwLock, maxFileSizeBytes, maxFiles);
        _reader = new FileLogReader(logDir, _rwLock, _writer);
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, _writer);

    public void Dispose()
    {
        _writer.Dispose();
        _rwLock.Dispose();
    }

    private class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLogWriter _writer;

        public FileLogger(string category, FileLogWriter writer)
        {
            _category = category;
            _writer = writer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var entry = new FileLogEntry(
                Timestamp: DateTime.UtcNow,
                Level: logLevel.ToString(),
                Category: _category,
                Message: formatter(state, exception),
                Exception: exception?.ToString(),
                Source: "native"
            );

            _writer.Write(entry);
        }
    }
}
