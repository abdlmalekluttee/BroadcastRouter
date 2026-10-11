using BroadcastRouter.Infrastructure;

namespace BroadcastRouter.Web.Services;

/// <summary>Router and dependency warnings use bounded private files, not Application Event Log.</summary>
public sealed class RouterFileLoggerProvider(SupervisionTelemetry telemetry) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(telemetry, categoryName);
    public void Dispose() { } // Program owns and drains the shared telemetry writer.

    private sealed class FileLogger(SupervisionTelemetry telemetry, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            try { telemetry.RouterLog(level.ToString(), category, formatter(state, exception) + (exception is null ? "" : " | " + exception.Message)); }
            catch { } // Logging must never alter route/process execution.
        }
    }
}
