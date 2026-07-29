namespace Nimono;

internal static class ScanLogger
{
    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "scan.log");

    public static void Reset(string firstLine)
    {
        try { File.WriteAllText(Path, Format(firstLine)); } catch { }
    }

    public static void Log(string msg)
    {
        try { File.AppendAllText(Path, Format(msg)); } catch { }
    }

    private static string Format(string msg) => $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
}
