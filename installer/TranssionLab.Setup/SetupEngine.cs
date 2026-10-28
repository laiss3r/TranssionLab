using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace TranssionLab.Setup;

internal sealed class SetupInstallOptions
{
    public string InstallDirectory { get; set; } = SetupEngine.GetDefaultInstallDirectory();
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
}

internal static class SetupEngine
{
    public const string AppName = "TranssionLab";
    public const string AppFullName = "TranssionLab Universal Android Servicing Suite";
    public const string AppPublisher = "TranssionLab Open Servicing Engineering";
    public const string AppVersion = "1.9.0";
    public const string MainExeName = "TranssionLab.exe";
    public const string UninstallerExeName = "Uninstall-TranssionLab.exe";
    public const string EmbeddedBundleResourceName = "TranssionLabBundle.zip";
    private const string UninstallRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TranssionLab";

    public static string GetDefaultInstallDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", AppName);
    }

    public static string GetUserDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, AppName);
    }

    public static void Install(
        SetupInstallOptions options,
        Action<int, int, string>? onProgress = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var installRoot = Path.GetFullPath(options.InstallDirectory.Trim().Trim('"'));
        Directory.CreateDirectory(installRoot);
        var normalizedRootPrefix = installRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? installRoot
            : installRoot + Path.DirectorySeparatorChar;

        using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedBundleResourceName);
        if (resourceStream is null)
        {
            throw new InvalidOperationException(
                "Embedded application bundle ('TranssionLabBundle.zip') was not found inside this setup executable.");
        }

        // Remove stale top-level runtime binaries from any earlier version while keeping user data untouched
        CleanPriorRuntimeBinaries(installRoot);

        long totalBytesWritten = 0;
        using (var archive = new ZipArchive(resourceStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            int totalEntries = archive.Entries.Count;
            int index = 0;
            var copyBuffer = new byte[81920];

            foreach (var entry in archive.Entries)
            {
                index++;
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                var destinationPath = Path.GetFullPath(Path.Combine(installRoot, relativePath));
                if (!destinationPath.StartsWith(normalizedRootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Blocked archive path outside installation directory: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                var parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    Directory.CreateDirectory(parentDir!);
                }

                onProgress?.Invoke(index, totalEntries, $"Installing ({index}/{totalEntries}): {entry.FullName}");

                using var entryStream = entry.Open();
                using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                int read;
                while ((read = entryStream.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
                {
                    fileStream.Write(copyBuffer, 0, read);
                    totalBytesWritten += read;
                }
            }
        }

        // Provision local per-user workspace (%LOCALAPPDATA%\TranssionLab) directly in-process
        onProgress?.Invoke(98, 100, "Provisioning local offline workspace & TECNO / Infinix / itel profiles...");
        ProvisionLocalWorkspaceDirect(installRoot);

        // Install clean uninstaller executable & per-user Windows Installed Apps entry
        var currentSetupExe = Process.GetCurrentProcess().MainModule?.FileName;
        var uninstallerPath = Path.Combine(installRoot, UninstallerExeName);
        if (!string.IsNullOrWhiteSpace(currentSetupExe) &&
            File.Exists(currentSetupExe) &&
            !string.Equals(Path.GetFullPath(currentSetupExe), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(currentSetupExe!, uninstallerPath, overwrite: true);
            }
            catch
            {
                // Non-fatal if locked during in-place update
            }
        }

        RegisterPerUserUninstallEntry(installRoot, uninstallerPath, totalBytesWritten);

        var installedMainExe = Path.Combine(installRoot, MainExeName);
        if (options.CreateDesktopShortcut)
        {
            onProgress?.Invoke(99, 100, "Creating Desktop shortcut...");
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktopDir))
            {
                ShellShortcutHelper.CreateShortcut(
                    Path.Combine(desktopDir, $"{AppName}.lnk"),
                    installedMainExe,
                    installRoot,
                    AppFullName);
            }
        }

        if (options.CreateStartMenuShortcut)
        {
            onProgress?.Invoke(99, 100, "Creating Start Menu shortcuts...");
            var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrWhiteSpace(programsDir))
            {
                var menuFolder = Path.Combine(programsDir, AppName);
                ShellShortcutHelper.CreateShortcut(
                    Path.Combine(menuFolder, $"{AppName}.lnk"),
                    installedMainExe,
                    installRoot,
                    AppFullName);

                if (File.Exists(uninstallerPath))
                {
                    ShellShortcutHelper.CreateShortcut(
                        Path.Combine(menuFolder, $"Uninstall {AppName}.lnk"),
                        uninstallerPath,
                        installRoot,
                        $"Uninstall {AppFullName}",
                        "--uninstall");
                }
            }
        }

        onProgress?.Invoke(100, 100, $"Installation complete: {installedMainExe}");
    }

    public static void Uninstall(string installRoot)
    {
        var fullRoot = Path.GetFullPath(installRoot);
        var markerExe = Path.Combine(fullRoot, MainExeName);
        var markerDll = Path.Combine(fullRoot, "TranssionLab.dll");

        // Safety check: only delete runtime contents when inside an actual TranssionLab installation directory
        if (!File.Exists(markerExe) && !File.Exists(markerDll))
        {
            return;
        }

        // 1. Remove Desktop & Start Menu shortcuts
        TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"));
        var startMenuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        TryDeleteDirectory(startMenuFolder);

        // 2. Remove sub-folders installed by bundle
        foreach (var sub in new[] { "platform-tools", "profiles", "docs", "tools" })
        {
            TryDeleteDirectory(Path.Combine(fullRoot, sub));
        }

        // 3. Remove top-level runtime files (skipping currently running Uninstall-TranssionLab.exe if locked)
        var selfPath = Process.GetCurrentProcess().MainModule?.FileName;
        foreach (var file in Directory.GetFiles(fullRoot))
        {
            if (!string.IsNullOrWhiteSpace(selfPath) &&
                string.Equals(Path.GetFullPath(file), Path.GetFullPath(selfPath!), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            TryDeleteFile(file);
        }

        // 4. Remove HKCU Installed Apps registry entry
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistrySubKey, throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    private static void CleanPriorRuntimeBinaries(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            return;
        }

        foreach (var pattern in new[] { "*.dll", "*.json", "*.pdb", MainExeName })
        {
            foreach (var f in Directory.GetFiles(installRoot, pattern, SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(f);
            }
        }
    }

    private static void ProvisionLocalWorkspaceDirect(string installRoot)
    {
        var dataRoot = GetUserDataDirectory();
        foreach (var sub in new[] { "logs", "history", "profiles", "staging_images", "partition_backups", "exports" })
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, sub));
        }

        // Seed bundled reference device profiles without overwriting user-edited profiles
        var bundledProfilesDir = Path.Combine(installRoot, "profiles");
        var userProfilesDir = Path.Combine(dataRoot, "profiles");
        if (Directory.Exists(bundledProfilesDir))
        {
            foreach (var srcProfile in Directory.GetFiles(bundledProfilesDir, "*.json"))
            {
                var destProfile = Path.Combine(userProfilesDir, Path.GetFileName(srcProfile));
                if (!File.Exists(destProfile))
                {
                    File.Copy(srcProfile, destProfile, overwrite: false);
                }
            }
        }

        // Pre-bind bundled platform-tools in settings.json if settings.json does not exist yet
        var settingsPath = Path.Combine(dataRoot, "settings.json");
        var bundledAdb = Path.Combine(installRoot, "platform-tools", "adb.exe");
        var bundledFastboot = Path.Combine(installRoot, "platform-tools", "fastboot.exe");
        if (!File.Exists(settingsPath) && File.Exists(bundledAdb) && File.Exists(bundledFastboot))
        {
            var escapedAdb = EscapeJsonString(bundledAdb);
            var escapedFb = EscapeJsonString(bundledFastboot);
            var json = "{\n" +
                       $"  \"CustomAdbExecutablePath\": \"{escapedAdb}\",\n" +
                       $"  \"CustomFastbootExecutablePath\": \"{escapedFb}\"\n" +
                       "}\n";
            File.WriteAllText(settingsPath, json, Encoding.UTF8);
        }

        var markerPath = Path.Combine(dataRoot, "first_run_setup.json");
        var markerJson = "{\n" +
                         $"  \"InitializedVersion\": \"{AppVersion}\",\n" +
                         $"  \"InitializedAtUtc\": \"{DateTimeOffset.UtcNow:O}\",\n" +
                         $"  \"InstallDirectory\": \"{EscapeJsonString(installRoot)}\"\n" +
                         "}\n";
        File.WriteAllText(markerPath, markerJson, Encoding.UTF8);
    }

    private static void RegisterPerUserUninstallEntry(string installRoot, string uninstallerPath, long totalBytesWritten)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistrySubKey);
            if (key is null)
            {
                return;
            }

            var mainExe = Path.Combine(installRoot, MainExeName);
            int estimatedKb = (int)Math.Max(1024L, totalBytesWritten / 1024L);

            key.SetValue("DisplayName", AppFullName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
            key.SetValue("Publisher", AppPublisher, RegistryValueKind.String);
            key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
            key.SetValue("DisplayIcon", $"{mainExe},0", RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall /S", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
        }
        catch
        {
            // Non-fatal in restricted sandbox environments
        }
    }

    private static string EscapeJsonString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
