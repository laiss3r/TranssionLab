using System.IO.Compression;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Core.Services;

/// <summary>
/// Handles deterministic first-time setup and local workspace provisioning:
/// - Creates required workspace directories under %LOCALAPPDATA%\TranssionLab (standard user, zero elevation).
/// - Seeds bundled reference TECNO/Infinix/itel JSON profiles without overwriting user customizations.
/// - Auto-discovers installed Android SDK Platform-Tools (adb &amp; fastboot) across app/workspace/SDK/PATH locations.
/// - Optionally downloads official Google Android SDK Platform-Tools (Apache-2.0) when explicitly requested,
///   degrading gracefully when offline so installation and core workflows remain 100% offline-first.
/// </summary>
public sealed class FirstTimeSetupService : IFirstTimeSetupService
{
    public static readonly Uri OfficialGooglePlatformToolsWindowsUri =
        new("https://dl.google.com/android/repository/platform-tools-latest-windows.zip");

    private static readonly string[] WorkspaceSubdirectories =
    [
        "logs",
        "history",
        "profiles",
        "staging_images",
        "partition_backups",
        "exports",
        "tools",
        "platform-tools"
    ];

    private readonly IAppSettingsService _settingsService;
    private readonly string _applicationBaseDirectory;
    private readonly HttpMessageHandler? _customHttpHandler;
    private readonly Uri _platformToolsArchiveUri;

    public FirstTimeSetupService(
        IAppSettingsService settingsService,
        string? customApplicationBaseDirectory = null,
        HttpMessageHandler? customHttpHandler = null,
        Uri? customPlatformToolsUri = null)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
        _applicationBaseDirectory = string.IsNullOrWhiteSpace(customApplicationBaseDirectory)
            ? AppContext.BaseDirectory
            : customApplicationBaseDirectory;
        _customHttpHandler = customHttpHandler;
        _platformToolsArchiveUri = customPlatformToolsUri ?? OfficialGooglePlatformToolsWindowsUri;
    }

    public async Task<FirstTimeSetupResult> InitializeEnvironmentAsync(
        bool downloadPlatformToolsIfMissing = false,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default)
    {
        var current = _settingsService.Current;
        var workspaceDir = string.IsNullOrWhiteSpace(current.WorkspaceDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TranssionLab")
            : current.WorkspaceDataDirectory;

        statusProgress?.Report($"Initializing local workspace at {workspaceDir}...");
        Directory.CreateDirectory(workspaceDir);
        foreach (var sub in WorkspaceSubdirectories)
        {
            Directory.CreateDirectory(Path.Combine(workspaceDir, sub));
        }

        // Seed bundled reference device profiles into workspace/profiles without overwriting user edits
        int seededProfiles = SeedBundledProfiles(workspaceDir, statusProgress);

        // Discover existing adb & fastboot binaries locally
        var (adbPath, adbFound) = LocateToolBinary(current.AdbExecutablePath, "adb", workspaceDir);
        var (fastbootPath, fastbootFound) = LocateToolBinary(current.FastbootExecutablePath, "fastboot", workspaceDir);

        bool downloadedTools = false;
        string? downloadNote = null;

        if (downloadPlatformToolsIfMissing && (!adbFound || !fastbootFound))
        {
            statusProgress?.Report("Downloading official Google Android SDK Platform-Tools (adb & fastboot)...");
            var (ok, note) = await TryDownloadAndExtractPlatformToolsAsync(workspaceDir, statusProgress, ct).ConfigureAwait(false);
            downloadedTools = ok;
            downloadNote = note;

            if (ok)
            {
                (adbPath, adbFound) = LocateToolBinary("adb", "adb", workspaceDir);
                (fastbootPath, fastbootFound) = LocateToolBinary("fastboot", "fastboot", workspaceDir);
            }
        }

        var updatedSettings = current with
        {
            WorkspaceDataDirectory = workspaceDir,
            AdbExecutablePath = adbFound ? adbPath : current.AdbExecutablePath,
            FastbootExecutablePath = fastbootFound ? fastbootPath : current.FastbootExecutablePath,
            FirstTimeSetupCompleted = true,
            FirstSetupTimestampUtc = current.FirstSetupTimestampUtc ?? DateTimeOffset.UtcNow
        };

        await _settingsService.SaveAsync(updatedSettings, ct).ConfigureAwait(false);

        var toolSummary = adbFound && fastbootFound
            ? $"Android Platform-Tools ready (adb: {adbPath}; fastboot: {fastbootPath})."
            : "Android Platform-Tools (adb/fastboot) not yet installed locally — offline firmware, profile, and USB tools are ready, and you can auto-install Platform-Tools anytime in Settings (#18).";

        if (!string.IsNullOrWhiteSpace(downloadNote) && !downloadedTools)
        {
            toolSummary = $"{downloadNote} Offline servicing features remain fully ready.";
        }

        var summary = $"First-time setup complete. Workspace: {workspaceDir} ({seededProfiles} new profile(s) seeded). {toolSummary}";
        statusProgress?.Report(summary);

        return new FirstTimeSetupResult(
            Succeeded: true,
            WorkspaceDirectory: workspaceDir,
            SeededProfileCount: seededProfiles,
            ResolvedAdbPath: updatedSettings.AdbExecutablePath,
            AdbAvailable: adbFound,
            ResolvedFastbootPath: updatedSettings.FastbootExecutablePath,
            FastbootAvailable: fastbootFound,
            PlatformToolsDownloaded: downloadedTools,
            SummaryMessage: summary);
    }

    private int SeedBundledProfiles(string workspaceDir, IProgress<string>? statusProgress)
    {
        var bundledProfilesDir = Path.Combine(_applicationBaseDirectory, "profiles");
        if (!Directory.Exists(bundledProfilesDir))
        {
            return 0;
        }

        var targetProfilesDir = Path.Combine(workspaceDir, "profiles");
        Directory.CreateDirectory(targetProfilesDir);

        int seeded = 0;
        foreach (var srcFile in Directory.EnumerateFiles(bundledProfilesDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(srcFile);
            var destFile = Path.Combine(targetProfilesDir, fileName);
            if (!File.Exists(destFile))
            {
                File.Copy(srcFile, destFile, overwrite: false);
                seeded++;
            }
        }

        if (seeded > 0)
        {
            statusProgress?.Report($"Seeded {seeded} reference TECNO/Infinix/itel device profile(s).");
        }

        return seeded;
    }

    private (string ResolvedPath, bool ExistsLocally) LocateToolBinary(string? configuredPath, string toolBaseName, string workspaceDir)
    {
        var exeName = OperatingSystem.IsWindows() ? $"{toolBaseName}.exe" : toolBaseName;

        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            !string.Equals(configuredPath.Trim(), toolBaseName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuredPath.Trim(), exeName, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(configuredPath.Trim()))
        {
            return (Path.GetFullPath(configuredPath.Trim()), true);
        }

        var candidates = new List<string>
        {
            Path.Combine(_applicationBaseDirectory, "platform-tools", exeName),
            Path.Combine(_applicationBaseDirectory, "tools", exeName),
            Path.Combine(_applicationBaseDirectory, exeName),
            Path.Combine(workspaceDir, "platform-tools", exeName),
            Path.Combine(workspaceDir, "tools", exeName)
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", exeName));
            candidates.Add(Path.Combine(localAppData, "Programs", "TranssionLab", "platform-tools", exeName));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(segment, exeName));
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH segments
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return (Path.GetFullPath(candidate), true);
            }
        }

        return (toolBaseName, false);
    }

    private async Task<(bool Downloaded, string Note)> TryDownloadAndExtractPlatformToolsAsync(
        string workspaceDir,
        IProgress<string>? statusProgress,
        CancellationToken ct)
    {
        var tempZipPath = Path.Combine(workspaceDir, $"platform-tools-setup-{Guid.NewGuid():N}.zip");
        var destinationRoot = Path.GetFullPath(Path.Combine(workspaceDir, "platform-tools"));
        Directory.CreateDirectory(destinationRoot);

        try
        {
            using var client = _customHttpHandler is not null
                ? new HttpClient(_customHttpHandler, disposeHandler: false)
                : new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(3);

            using var response = await client.GetAsync(
                _platformToolsArchiveUri,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Official Platform-Tools server returned HTTP {(int)response.StatusCode}.");
            }

            await using (var netStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var outFile = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await netStream.CopyToAsync(outFile, ct).ConfigureAwait(false);
            }

            statusProgress?.Report("Extracting and verifying Android SDK Platform-Tools...");
            ExtractPlatformToolsZipSafely(tempZipPath, destinationRoot);

            var expectedAdb = Path.Combine(destinationRoot, OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            if (!File.Exists(expectedAdb))
            {
                return (false, "Downloaded archive did not contain expected adb executable.");
            }

            return (true, $"Installed official Android SDK Platform-Tools into {destinationRoot}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Skipped online Platform-Tools download (offline or unreachable: {ex.Message}).");
        }
        catch (TaskCanceledException)
        {
            return (false, "Skipped online Platform-Tools download (connection timed out).");
        }
        catch (InvalidDataException ex)
        {
            return (false, $"Downloaded Platform-Tools archive was invalid ({ex.Message}).");
        }
        catch (IOException ex)
        {
            return (false, $"Could not write Platform-Tools files ({ex.Message}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, $"Permission denied writing Platform-Tools ({ex.Message}).");
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup
            }
        }
    }

    private static void ExtractPlatformToolsZipSafely(string zipPath, string destinationRoot)
    {
        var normalizedRoot = Path.GetFullPath(destinationRoot);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedRoot += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (relative.StartsWith("platform-tools/", StringComparison.OrdinalIgnoreCase))
            {
                relative = relative["platform-tools/".Length..];
            }

            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!targetPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Blocked unsafe archive entry path '{entry.FullName}'.");
            }

            var parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }
}
