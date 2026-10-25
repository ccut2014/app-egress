using System.IO;
using AppEgress.Config;

namespace AppEgress.Core;

/// <summary>Журнал в памяти (последние записи для UI) и в файле %AppData%\AppEgress\log.txt.</summary>
internal static class Logger
{
    private const int MaxLines = 500;
    private const long MaxFileSize = 2 * 1024 * 1024;
    private static readonly object Lock = new();
    private static readonly LinkedList<string> Lines = new();
    private static string? _lastMessage;
    private static long _lastTick;

    public static event Action<string>? Written;

    public static string FilePath => Path.Combine(SettingsStore.Directory, "log.txt");

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Lock)
        {
            // Одинаковые ошибки из рабочих потоков пишем не чаще раза в секунду.
            long now = Environment.TickCount64;
            if (message == _lastMessage && now - _lastTick < 1000)
                return;
            _lastMessage = message;
            _lastTick = now;

            Lines.AddLast(line);
            while (Lines.Count > MaxLines)
                Lines.RemoveFirst();
            try
            {
                Directory.CreateDirectory(SettingsStore.Directory);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxFileSize)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Written?.Invoke(line);
    }

    public static string[] Snapshot()
    {
        lock (Lock)
            return Lines.ToArray();
    }
}
