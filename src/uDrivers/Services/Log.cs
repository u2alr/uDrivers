namespace uDrivers.Services;

/// <summary>Tiny append-only file log at %LocalAppData%\uDrivers\logs.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Folder => Path.Combine(AppInfo.DataFolder, "logs");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd}.log");
            lock (Gate)
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
