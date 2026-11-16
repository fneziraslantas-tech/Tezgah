using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Tezgah.App.Services;

/// <summary>Minimal daily-rolling file logger at <c>%LOCALAPPDATA%\Tezgah\logs\tezgah-yyyyMMdd.log</c>.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();

    public FileLoggerProvider(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tezgah", "logs");

    public string Directory { get; }

    public string CurrentFile => Path.Combine(Directory, $"tezgah-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var sb = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ").Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            sb.AppendLine().Append(exception);
        }

        sb.AppendLine();
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentFile, sb.ToString());
            }
            catch (IOException)
            {
                // logging must never crash the app
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
