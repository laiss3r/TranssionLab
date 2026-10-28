using System;
using System.IO;
using System.Linq;
using System.Windows;
using TranssionLab.Core.Services;

namespace TranssionLab.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        args ??= Array.Empty<string>();
        bool isSelfTest = args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
        bool isFirstRunInit = args.Any(a => string.Equals(a, "--first-run-init", StringComparison.OrdinalIgnoreCase));
        bool downloadTools = args.Any(a => string.Equals(a, "--download-platform-tools", StringComparison.OrdinalIgnoreCase));

        try
        {
            if (isFirstRunInit)
            {
                var settingsSvc = new AppSettingsService();
                var setupSvc = new FirstTimeSetupService(settingsSvc);
                setupSvc.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: downloadTools)
                    .GetAwaiter()
                    .GetResult();
                return 0;
            }

            var app = new App();
            app.InitializeComponent();

            if (isSelfTest)
            {
                return app.RunHeadlessVisualSelfTest();
            }

            return app.Run();
        }
        catch (Exception ex)
        {
            var crashPath = WriteBootstrapCrashLog(ex);
            if (!isSelfTest && !isFirstRunInit)
            {
                try
                {
                    MessageBox.Show(
                        $"TranssionLab encountered a startup error.\n\nDiagnostic details were saved locally to:\n{crashPath}\n\n{ex.GetType().Name}: {ex.Message}",
                        "TranssionLab Startup Diagnostic",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // Ignore MessageBox failure in headless sessions
                }
            }
            else
            {
                Console.Error.WriteLine($"BOOTSTRAP_ERROR: {ex}");
            }

            return 1;
        }
    }

    private static string WriteBootstrapCrashLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TranssionLab",
                "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"startup-crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(
                path,
                $"TranssionLab v1.8.0 Bootstrap Diagnostic Report\nTimestampUtc: {DateTimeOffset.UtcNow:O}\nBaseDirectory: {AppContext.BaseDirectory}\nOS: {Environment.OSVersion}\nException:\n{ex}\n");
            return path;
        }
        catch
        {
            return "(local log directory unavailable)";
        }
    }
}
