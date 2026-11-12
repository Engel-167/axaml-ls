namespace AxamlLs;

/// <summary>Logs to stderr, which Zed shows in the language server log ("dev: open language server logs").</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static bool Verbose { get; set; }

    public static void Info(string message) => Write("info", message);
    public static void Debug(string message)
    {
        if (Verbose) Write("debug", message);
    }
    public static void Error(string message, Exception? ex = null) =>
        Write("error", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level}: {message}");
        }
    }
}
