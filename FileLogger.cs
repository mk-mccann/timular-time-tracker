namespace TimeTracker;

/// <summary>Appends log messages to a file, since there is no console to show them.</summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider, ILogger
{
    readonly object gate = new();

    public ILogger CreateLogger(string category) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public void Dispose() { }

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format)
    {
        if (!IsEnabled(level)) return;
        var message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level}: {format(state, e)}{(e == null ? "" : Environment.NewLine + e)}{Environment.NewLine}";
        lock (gate) File.AppendAllText(path, message);
    }
}
