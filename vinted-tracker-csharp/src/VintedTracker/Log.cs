namespace VintedTracker;

/// <summary>
/// Prosty logger: pisze na konsolę i trzyma ostatnie ~800 linii w pamięci,
/// żeby dashboard mógł je oddać jednym przyciskiem (📋 Logi) do wklejenia
/// przy zgłaszaniu problemów / dostrajaniu.
/// </summary>
public static class Log
{
    private const int MaxLines = 800;
    private static readonly object Sync = new();
    private static readonly Queue<string> Buffer = new();

    public static void Info(string message) => Write("info", message, Console.Out);
    public static void Warn(string message) => Write("warn", message, Console.Error);
    public static void Error(string message) => Write("error", message, Console.Error);

    private static void Write(string level, string message, TextWriter console)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        console.WriteLine(line);
        lock (Sync)
        {
            Buffer.Enqueue(line);
            while (Buffer.Count > MaxLines)
                Buffer.Dequeue();
        }
    }

    public static string Dump()
    {
        lock (Sync)
            return string.Join('\n', Buffer);
    }
}
