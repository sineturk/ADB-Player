using System.Text;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.Infrastructure;

public sealed class FileAppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly AppPaths _paths;

    public FileAppLogger(AppPaths paths)
    {
        _paths = paths;
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warning(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var file = Path.Combine(
                _paths.LogDirectory,
                $"player-{DateTimeOffset.Now:yyyyMMdd}.log");

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("O"));
            builder.Append(" [");
            builder.Append(level);
            builder.Append("] ");
            builder.AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (_gate)
            {
                File.AppendAllText(file, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Günlük yazma hatası oynatıcıyı durdurmamalıdır.
        }
    }
}
