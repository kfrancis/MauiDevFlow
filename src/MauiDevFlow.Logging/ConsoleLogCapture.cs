using System.Diagnostics;
using System.Text;

namespace MauiDevFlow.Logging;

/// <summary>
/// Captures Console.Write/WriteLine and Trace/Debug output into the FileLogWriter pipeline.
/// Output is tee'd — the original console stream still receives everything.
/// Call <see cref="Install"/> once after the FileLogWriter is available.
/// Call <see cref="Uninstall"/> (or Dispose) to restore original streams and listeners.
/// </summary>
public sealed class ConsoleLogCapture : IDisposable
{
    private readonly FileLogWriter _writer;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly LogTextWriter _outWriter;
    private readonly LogTextWriter _errorWriter;
    private readonly LogTraceListener _traceListener;
    private volatile bool _installed;
    private volatile bool _disposed;

    public ConsoleLogCapture(FileLogWriter writer)
    {
        _writer = writer;
        _originalOut = Console.Out;
        _originalError = Console.Error;
        _outWriter = new LogTextWriter(_originalOut, writer, "console.out");
        _errorWriter = new LogTextWriter(_originalError, writer, "console.error");
        _traceListener = new LogTraceListener(writer);
    }

    /// <summary>
    /// Redirects Console.Out, Console.Error, and/or adds a TraceListener based on flags.
    /// Safe to call multiple times — only installs once.
    /// </summary>
    public void Install(bool captureConsole = true, bool captureTrace = true)
    {
        if (_installed || _disposed) return;
        _installed = true;

        if (captureConsole)
        {
            Console.SetOut(_outWriter);
            Console.SetError(_errorWriter);
        }

        if (captureTrace)
        {
            Trace.Listeners.Add(_traceListener);
        }
    }

    /// <summary>
    /// Restores original Console streams and removes the TraceListener.
    /// </summary>
    public void Uninstall()
    {
        if (!_installed) return;
        _installed = false;

        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Trace.Listeners.Remove(_traceListener);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }

    /// <summary>
    /// A TextWriter that tees output to both the original stream and the FileLogWriter.
    /// Buffers partial writes and flushes complete lines as log entries.
    /// </summary>
    private sealed class LogTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly FileLogWriter _logWriter;
        private readonly string _source;
        private readonly StringBuilder _lineBuffer = new();
        private readonly object _lock = new();

        public LogTextWriter(TextWriter inner, FileLogWriter logWriter, string source)
        {
            _inner = inner;
            _logWriter = logWriter;
            _source = source;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);

            lock (_lock)
            {
                if (value == '\n')
                    FlushLine();
                else
                    _lineBuffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            _inner.Write(value);

            lock (_lock)
            {
                foreach (var ch in value)
                {
                    if (ch == '\n')
                        FlushLine();
                    else
                        _lineBuffer.Append(ch);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);

            lock (_lock)
            {
                _lineBuffer.Append(value);
                FlushLine();
            }
        }

        public override void WriteLine()
        {
            _inner.WriteLine();

            lock (_lock)
            {
                FlushLine();
            }
        }

        public override void Flush()
        {
            _inner.Flush();

            lock (_lock)
            {
                if (_lineBuffer.Length > 0)
                    FlushLine();
            }
        }

        private void FlushLine()
        {
            var line = _lineBuffer.ToString().TrimEnd('\r');
            _lineBuffer.Clear();

            if (string.IsNullOrEmpty(line)) return;

            _logWriter.Write(new FileLogEntry(
                Timestamp: DateTime.UtcNow,
                Level: _source == "console.error" ? "Warning" : "Information",
                Category: _source,
                Message: line,
                Source: _source
            ));
        }
    }

    /// <summary>
    /// TraceListener that writes Trace/Debug output to the FileLogWriter.
    /// </summary>
    private sealed class LogTraceListener : TraceListener
    {
        private readonly FileLogWriter _logWriter;
        private readonly StringBuilder _lineBuffer = new();
        private readonly object _lock = new();

        public LogTraceListener(FileLogWriter logWriter) : base("MauiDevFlowTrace")
        {
            _logWriter = logWriter;
        }

        public override void Write(string? message)
        {
            if (message == null) return;

            lock (_lock)
            {
                _lineBuffer.Append(message);
            }
        }

        public override void WriteLine(string? message)
        {
            lock (_lock)
            {
                _lineBuffer.Append(message);
                FlushLine();
            }
        }

        public override void Flush()
        {
            lock (_lock)
            {
                if (_lineBuffer.Length > 0)
                    FlushLine();
            }
        }

        private void FlushLine()
        {
            var line = _lineBuffer.ToString();
            _lineBuffer.Clear();

            if (string.IsNullOrEmpty(line)) return;

            _logWriter.Write(new FileLogEntry(
                Timestamp: DateTime.UtcNow,
                Level: "Debug",
                Category: "trace",
                Message: line,
                Source: "trace"
            ));
        }
    }
}
