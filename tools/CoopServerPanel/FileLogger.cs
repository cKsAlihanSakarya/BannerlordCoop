using System.Text;

namespace CoopServerPanel;

/// <summary>
/// Writes the panel's log lines to panel.log beside the exe as well as the console, so a problem
/// can be read after the window is closed. The file starts over when it passes 5 MB.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly object gate = new();
    private readonly string path;
    private StreamWriter writer;

    public FileLoggerProvider(string path)
    {
        this.path = path;
        Open(append: File.Exists(path) && new FileInfo(path).Length < MaxBytes);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (gate)
        {
            try
            {
                if (writer.BaseStream.Length > MaxBytes)
                {
                    writer.Dispose();
                    Open(append: false);
                }
                writer.WriteLine(line);
                writer.Flush();
            }
            catch (IOException)
            {
                // Logging must never stop the panel.
            }
        }
    }

    private void Open(bool append)
    {
        var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer = new StreamWriter(stream, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        lock (gate) writer?.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider provider;
        private readonly string category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            this.provider = provider;
            int dot = category.LastIndexOf('.');
            this.category = dot >= 0 ? category.Substring(dot + 1) : category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Level(logLevel)} {category}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            provider.Write(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => level.ToString().ToUpperInvariant(),
        };
    }
}
