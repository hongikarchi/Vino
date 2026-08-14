using System.Text;

namespace Vino.AgentHost.Hosting;

/// <summary>
/// Minimal rolling file sink for host logs (host.log in the project data root). Deliberately
/// tiny: one file, size-capped with a single rollover (host.log → host.log.1), Information and
/// up, full exception type + stack (the whole point of the file — problem-log carries only the
/// message), and it never throws into the caller's logging path.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(level).Append("] ")
                .Append(category).Append(": ").Append(message);
            if (exception is not null)
            {
                line.AppendLine().Append(exception);
            }
            line.AppendLine();
            lock (_gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }
                File.AppendAllText(_path, line.ToString());
            }
        }
        catch (Exception)
        {
            // A full disk or a locked file must never take the host down through its logger.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
