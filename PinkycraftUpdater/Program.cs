using System.Text;

namespace PinkycraftUpdater;

internal static class Program
{
    internal static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "PinkycraftUpdater-error.log");

    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleFatal(e.ExceptionObject as Exception ?? new Exception("Unknown fatal error."));

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            HandleFatal(ex);
        }
    }

    internal static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n{ex}\r\n\r\n",
                Encoding.UTF8);
        }
        catch
        {
            // Logging must never cause another crash.
        }
    }

    private static void HandleFatal(Exception ex)
    {
        Log(ex);
        try
        {
            MessageBox.Show(
                $"The updater encountered an error.\n\nA log was saved here:\n{LogPath}",
                "Pinkycraft 2 Updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Last-resort handler.
        }
    }
}
