using Microsoft.Extensions.Logging;

namespace SampleMauiApp;

/// <summary>
/// Simple file-based logger for debugging MAUI apps.
/// Uses the app's container directory which is accessible even in sandboxed apps.
/// </summary>
public class DebugFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _logFilePath;
    private static readonly object _lock = new();

    public DebugFileLogger(string categoryName, string logFilePath)
    {
        _categoryName = categoryName;
        _logFilePath = logFilePath;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] [{_categoryName}] {formatter(state, exception)}";
        if (exception != null)
        {
            message += $"\n  Exception: {exception.GetType().Name}: {exception.Message}\n  {exception.StackTrace}";
        }

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, message + Environment.NewLine);
            }
            catch
            {
                // Ignore file write errors
            }
        }
    }
}

public class DebugFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly string _logFilePath;

    public DebugFileLoggerProvider(string appName)
    {
        _logDirectory = Path.Combine(FileSystem.CacheDirectory, "mauidevflow-debuglogs", appName);
        
        Directory.CreateDirectory(_logDirectory);
        
        _logFilePath = Path.Combine(_logDirectory, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        
        // Write header
        File.WriteAllText(_logFilePath, $"=== {appName} Debug Log Started at {DateTime.Now:O} ==={Environment.NewLine}");
    }

    public string LogFilePath => _logFilePath;

    public ILogger CreateLogger(string categoryName)
    {
        return new DebugFileLogger(categoryName, _logFilePath);
    }

    public void Dispose() { }
}

/// <summary>
/// Static helper for quick logging without DI.
/// Uses MAUI's sandboxed app data directory.
/// </summary>
public static class DebugLog
{
    private static string? _logFilePath;
    private static readonly object _lock = new();

    public static string? LogFilePath => _logFilePath;

    public static void Initialize(string appName)
    {
        try
        {
            // Use MAUI's FileSystem.AppDataDirectory - this is always writable in sandbox
            var logDir = Path.Combine(FileSystem.AppDataDirectory, "logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(_logFilePath, $"=== {appName} Debug Log Started at {DateTime.Now:O} ==={Environment.NewLine}");
            File.AppendAllText(_logFilePath, $"Log location: {_logFilePath}{Environment.NewLine}");
            System.Diagnostics.Debug.WriteLine($"[DebugLog] Logging to: {_logFilePath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DebugLog] Failed to create log: {ex.Message}");
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        
        // Also write to Debug output
        System.Diagnostics.Debug.WriteLine(line);
        
        if (_logFilePath == null) return;

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch { }
        }
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write($"[ERROR] {message}");
        if (ex != null)
        {
            Write($"  Exception: {ex.GetType().Name}: {ex.Message}");
            Write($"  StackTrace: {ex.StackTrace}");
        }
    }
}
