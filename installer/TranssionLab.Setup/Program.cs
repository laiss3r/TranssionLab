using System;
using System.IO;
using System.Windows.Forms;

namespace TranssionLab.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = false;
        bool uninstall = false;
        string? customDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i] ?? string.Empty;
            if (string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
            else if (string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                uninstall = true;
            }
            else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
            {
                // Support standard /D=C:\Path with or without spaces across remaining args
                var remainder = arg.Substring(3);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("/", StringComparison.Ordinal) && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    remainder = remainder + " " + string.Join(" ", args, i + 1, args.Length - (i + 1));
                    i = args.Length;
                }
                customDir = remainder.Trim().Trim('"');
            }
            else if (arg.StartsWith("--install-dir=", StringComparison.OrdinalIgnoreCase))
            {
                customDir = arg.Substring("--install-dir=".Length).Trim().Trim('"');
            }
        }

        if (uninstall)
        {
            var targetRoot = !string.IsNullOrWhiteSpace(customDir)
                ? customDir!
                : AppDomain.CurrentDomain.BaseDirectory;

            if (!silent)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var confirm = MessageBox.Show(
                    $"Uninstall {SetupEngine.AppFullName} from:\n{targetRoot}\n\n(Your saved logs, profiles, and backups in %LOCALAPPDATA%\\TranssionLab will be preserved.)",
                    $"Uninstall {SetupEngine.AppName}",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (confirm != DialogResult.Yes)
                {
                    return 0;
                }
            }

            SetupEngine.Uninstall(targetRoot);
            if (!silent)
            {
                MessageBox.Show(
                    $"{SetupEngine.AppFullName} has been removed.",
                    $"Uninstall {SetupEngine.AppName}",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return 0;
        }

        if (silent)
        {
            try
            {
                var options = new SetupInstallOptions
                {
                    InstallDirectory = !string.IsNullOrWhiteSpace(customDir)
                        ? customDir!
                        : SetupEngine.GetDefaultInstallDirectory(),
                    CreateDesktopShortcut = string.IsNullOrWhiteSpace(customDir),
                    CreateStartMenuShortcut = string.IsNullOrWhiteSpace(customDir)
                };
                SetupEngine.Install(options);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"SETUP_FAILED: {ex}");
                return 1;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var wizard = new SetupWizardForm();
        Application.Run(wizard);
        return 0;
    }
}
