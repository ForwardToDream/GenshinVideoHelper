namespace GenshinVideoHelper.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

public interface ILogSink
{
    // Called from any thread; implementations must not block or throw.
    void Write(LogLevel level, string source, string message, Exception? exception);
}

/// <summary>Process-wide diagnostics. Without a host-installed sink nothing is written, so tests stay silent.</summary>
public static class AppLog
{
    private static volatile ILogSink? _sink;
    private static volatile int _minLevel = (int)LogLevel.Info;

    public static ILogSink? Sink { get => _sink; set => _sink = value; }
    public static LogLevel MinLevel { get => (LogLevel)_minLevel; set => _minLevel = (int)value; }

    public static void Debug(string source, string message, Exception? exception = null) => Write(LogLevel.Debug, source, message, exception);
    public static void Info(string source, string message, Exception? exception = null) => Write(LogLevel.Info, source, message, exception);
    public static void Warn(string source, string message, Exception? exception = null) => Write(LogLevel.Warn, source, message, exception);
    public static void Error(string source, string message, Exception? exception = null) => Write(LogLevel.Error, source, message, exception);

    private static void Write(LogLevel level, string source, string message, Exception? exception)
    {
        if ((int)level < _minLevel || _sink is not { } sink) return;
        try { sink.Write(level, source, message, exception); }
        catch { /* Diagnostics never change application behavior. */ }
    }
}
