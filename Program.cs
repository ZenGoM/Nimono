namespace Nimono;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.ThreadException += (_, e) =>
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.WriteAllText(logPath, e.Exception.ToString());
            MessageBox.Show($"エラーが発生しました。\n\n{e.Exception.Message}\n\nログ: {logPath}",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.WriteAllText(logPath, e.ExceptionObject?.ToString() ?? "unknown");
        };
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
