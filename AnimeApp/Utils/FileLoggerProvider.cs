using Microsoft.Extensions.Logging;

namespace AnimeApp.Utils;
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly string filePath = path;
    private readonly object gate = new();
    public ILogger CreateLogger(string category) => new FileLogger(this, category);
    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level))
                return;
            lock (owner.gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(owner.filePath)!);
                if (File.Exists(owner.filePath) && new FileInfo(owner.filePath).Length > 2_000_000)
                    File.Move(owner.filePath, owner.filePath + ".1", true);
                File.AppendAllText(owner.filePath, $"{DateTimeOffset.Now:O} {level} {category}: {format(state, error)} {error?.GetType().Name}{Environment.NewLine}");
            }
        }
    }
}
