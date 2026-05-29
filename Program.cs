namespace SlinnerBMusicStudio;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleCrash(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleCrash(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) => HandleCrash(e.Exception, "Task");

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            HandleCrash(ex, "startup");
        }
    }

    private static int _crashCount;

    private static void HandleCrash(Exception? ex, string source)
    {
        if (Interlocked.Increment(ref _crashCount) > 1) return;

        string path;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            path = Path.Combine(desktop, $"SlinnerBMusicStudio-crash-{stamp}.log");

            var body =
                $"SlinnerB's Music Studio crash report{Environment.NewLine}" +
                $"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"Source:  {source}{Environment.NewLine}" +
                $"OS:      {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit){Environment.NewLine}" +
                $"Runtime: {Environment.Version}{Environment.NewLine}" +
                $"CLR:     {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
                $"Arch:    {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}" +
                $"Machine: {Environment.MachineName}{Environment.NewLine}" +
                $"{Environment.NewLine}--- Exception ---{Environment.NewLine}" +
                (ex?.ToString() ?? "(no exception object)");

            File.WriteAllText(path, body);
        }
        catch
        {
            path = "(could not write log file)";
        }

        try
        {
            MessageBox.Show(
                $"SlinnerB's Music Studio crashed.{Environment.NewLine}{Environment.NewLine}" +
                $"A crash log has been saved to your Desktop:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}" +
                $"Please send this file back so the issue can be fixed.",
                "SlinnerB's Music Studio - Crash",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch { /* nothing more we can do */ }

        Environment.Exit(1);
    }
}
