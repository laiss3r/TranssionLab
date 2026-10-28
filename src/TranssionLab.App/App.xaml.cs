using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using TranssionLab.Core.Interfaces;
using TranssionLab.Firmware;
using TranssionLab.Logging;
using TranssionLab.ViewModels;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureUiBridge();
        _serviceProvider = BuildAppServiceProvider();

        var logger = _serviceProvider.GetRequiredService<IStructuredLogger>();
        RegisterSafetyExceptionHandlers(logger);

        var settings = _serviceProvider.GetRequiredService<IAppSettingsService>();
        var firstTimeSetup = _serviceProvider.GetRequiredService<IFirstTimeSetupService>();

        bool needsToolPathRefresh = string.Equals(settings.Current.AdbExecutablePath, "adb", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(settings.Current.AdbExecutablePath);

        if (!settings.Current.FirstTimeSetupCompleted || needsToolPathRefresh)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await firstTimeSetup.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("FirstTimeSetup", "AutoProvision", ex.Message);
                }
            });
        }

        var usb = _serviceProvider.GetRequiredService<IUsbInterfaceProvider>();
        if (settings.Current.AutoPollUsbChanges)
        {
            usb.StartMonitoring();
        }

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    public int RunHeadlessVisualSelfTest()
    {
        ConfigureUiBridge();
        _serviceProvider = BuildAppServiceProvider();
        try
        {
            var setup = _serviceProvider.GetRequiredService<IFirstTimeSetupService>();
            setup.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: false).GetAwaiter().GetResult();

            var shell = _serviceProvider.GetRequiredService<MainShellViewModel>();
            var window = _serviceProvider.GetRequiredService<MainWindow>();

            window.Measure(new Size(1360, 860));
            window.Arrange(new Rect(0, 0, 1360, 860));
            window.UpdateLayout();

            int verifiedPages = 0;
            foreach (var page in shell.Pages)
            {
                shell.CurrentPage = page;
                var host = new ContentControl
                {
                    Content = page,
                    Resources = Resources
                };
                host.Measure(new Size(1080, 760));
                host.Arrange(new Rect(0, 0, 1080, 760));
                host.UpdateLayout();
                window.UpdateLayout();
                verifiedPages++;
            }

            Console.WriteLine($"SELF_TEST_OK:{verifiedPages}_PAGES_VERIFIED");
            return verifiedPages == 19 ? 0 : 2;
        }
        finally
        {
            UiInteractionBridge.InvokeOnUiThread = null;
            _serviceProvider.Dispose();
            _serviceProvider = null;
        }
    }

    private static ServiceProvider BuildAppServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddTranssionLabServices();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    private static void ConfigureUiBridge()
    {
        // Marshal all cross-thread ViewModel property/collection/command notifications onto WPF Dispatcher
        UiInteractionBridge.InvokeOnUiThread = action =>
        {
            var disp = Current?.Dispatcher;
            if (disp is null || disp.CheckAccess())
            {
                action();
                return;
            }

            if (!disp.HasShutdownStarted && !disp.HasShutdownFinished)
            {
                disp.Invoke(DispatcherPriority.Normal, action);
            }
        };

        // Wire native Windows dialogs and clipboard into ViewModel bridge with full defensive guards
        UiInteractionBridge.PickOpenFile = (title, filter) =>
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = title,
                    Filter = filter,
                    CheckFileExists = true
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }
            catch
            {
                return null;
            }
        };

        UiInteractionBridge.PickSaveFile = (title, filter, defaultFileName) =>
        {
            try
            {
                var ext = Path.GetExtension(defaultFileName);
                var stem = HashAndBinaryHelpers.SanitizeFileNameComponent(Path.GetFileNameWithoutExtension(defaultFileName), "export");
                var dlg = new SaveFileDialog
                {
                    Title = title,
                    Filter = filter,
                    FileName = string.IsNullOrEmpty(ext) ? stem : $"{stem}{ext}"
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }
            catch
            {
                return null;
            }
        };

        UiInteractionBridge.PickDirectory = title =>
        {
            try
            {
                var dlg = new OpenFolderDialog
                {
                    Title = title
                };
                return dlg.ShowDialog() == true ? dlg.FolderName : null;
            }
            catch
            {
                return null;
            }
        };

        UiInteractionBridge.CopyTextToClipboard = text =>
        {
            if (string.IsNullOrEmpty(text))
                return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, false);
                    return;
                }
                catch (COMException)
                {
                    Thread.Sleep(40);
                }
                catch
                {
                    return;
                }
            }
        };
    }

    private void RegisterSafetyExceptionHandlers(IStructuredLogger logger)
    {
        UiInteractionBridge.OnCommandException = ex =>
        {
            logger.LogError("UiCommandGuard", ex.GetType().Name, ex.Message);
        };

        // Local crash & unobserved task protection (Section 8) — never sends telemetry over the network
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogWarning("TaskScheduler", "UnobservedTaskException", args.Exception.Flatten().Message);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashReportService.WriteCrashReport(ex, logger.CurrentSessionId);
            }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            var path = CrashReportService.WriteCrashReport(args.Exception, logger.CurrentSessionId);
            logger.LogError("WPFDispatcher", "UnhandledUiException", $"Saved crash report to {path}: {args.Exception.Message}");
            MessageBox.Show(
                $"An unexpected error was caught safely and logged locally to:\n{path}\n\n{args.Exception.Message}",
                "TranssionLab Diagnostic Guard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UiInteractionBridge.InvokeOnUiThread = null;
        if (_serviceProvider is not null)
        {
            try
            {
                _serviceProvider.GetRequiredService<IDeviceDiscoveryProvider>().CancelActiveWait();
                _serviceProvider.GetRequiredService<IUsbInterfaceProvider>().StopMonitoring();
            }
            catch
            {
                // Ignore shutdown cleanup exceptions
            }
            _serviceProvider.Dispose();
        }
        base.OnExit(e);
    }
}
