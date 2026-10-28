using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TranssionLab.Core.Services;
using TranssionLab.ViewModels.Pages;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class FirstTimeSetupAndBootstrapTests : IDisposable
{
    private readonly string _tempRoot;

    public FirstTimeSetupAndBootstrapTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tl-setup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock races
        }
    }

    [Fact]
    public async Task InitializeEnvironmentAsync_CreatesWorkspaceSeedsProfilesAndPreservesCustomEdits()
    {
        var workspaceDir = Path.Combine(_tempRoot, "workspace");
        var appBaseDir = Path.Combine(_tempRoot, "appbase");
        var bundledProfilesDir = Path.Combine(appBaseDir, "profiles");
        Directory.CreateDirectory(bundledProfilesDir);

        await File.WriteAllTextAsync(Path.Combine(bundledProfilesDir, "tecno-test.json"), "{\"profileId\":\"tecno-1\"}");
        await File.WriteAllTextAsync(Path.Combine(bundledProfilesDir, "infinix-test.json"), "{\"profileId\":\"infinix-1\"}");

        var settingsSvc = new AppSettingsService(workspaceDir);
        var setupSvc = new FirstTimeSetupService(settingsSvc, customApplicationBaseDirectory: appBaseDir);

        var firstRun = await setupSvc.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: false);

        Assert.True(firstRun.Succeeded);
        Assert.Equal(2, firstRun.SeededProfileCount);
        Assert.True(settingsSvc.Current.FirstTimeSetupCompleted);
        Assert.NotNull(settingsSvc.Current.FirstSetupTimestampUtc);

        foreach (var expectedSub in new[] { "logs", "history", "profiles", "staging_images", "partition_backups", "exports", "tools", "platform-tools" })
        {
            Assert.True(Directory.Exists(Path.Combine(workspaceDir, expectedSub)), $"Expected workspace subfolder '{expectedSub}'");
        }

        // Customize one seeded profile and ensure second run does NOT overwrite user edits
        var userEditedProfile = Path.Combine(workspaceDir, "profiles", "tecno-test.json");
        await File.WriteAllTextAsync(userEditedProfile, "{\"profileId\":\"user-customized\"}");

        var secondRun = await setupSvc.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: false);
        Assert.Equal(0, secondRun.SeededProfileCount);
        Assert.Contains("user-customized", await File.ReadAllTextAsync(userEditedProfile));
    }

    [Fact]
    public async Task InitializeEnvironmentAsync_AutoDetectsBundledPlatformTools()
    {
        var workspaceDir = Path.Combine(_tempRoot, "workspace-tools");
        var appBaseDir = Path.Combine(_tempRoot, "appbase-tools");
        var appPlatformTools = Path.Combine(appBaseDir, "platform-tools");
        Directory.CreateDirectory(appPlatformTools);

        var adbName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var fastbootName = OperatingSystem.IsWindows() ? "fastboot.exe" : "fastboot";
        await File.WriteAllTextAsync(Path.Combine(appPlatformTools, adbName), "stub-adb");
        await File.WriteAllTextAsync(Path.Combine(appPlatformTools, fastbootName), "stub-fastboot");

        var settingsSvc = new AppSettingsService(workspaceDir);
        var setupSvc = new FirstTimeSetupService(settingsSvc, customApplicationBaseDirectory: appBaseDir);

        var res = await setupSvc.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: false);

        Assert.True(res.AdbAvailable);
        Assert.True(res.FastbootAvailable);
        Assert.EndsWith(adbName, settingsSvc.Current.AdbExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(fastbootName, settingsSvc.Current.FastbootExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeEnvironmentAsync_DownloadsAndExtractsOfficialPlatformToolsWhenRequested()
    {
        var workspaceDir = Path.Combine(_tempRoot, "workspace-dl");
        var appBaseDir = Path.Combine(_tempRoot, "appbase-empty");
        Directory.CreateDirectory(appBaseDir);

        var adbName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var fastbootName = OperatingSystem.IsWindows() ? "fastboot.exe" : "fastboot";

        byte[] zipBytes = BuildZipArchive(
            ($"platform-tools/{adbName}", "adb-binary"),
            ($"platform-tools/{fastbootName}", "fastboot-binary"),
            ("platform-tools/NOTICE.txt", "Apache-2.0"));

        using var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        });

        var settingsSvc = new AppSettingsService(workspaceDir);
        var setupSvc = new FirstTimeSetupService(
            settingsSvc,
            customApplicationBaseDirectory: appBaseDir,
            customHttpHandler: handler,
            customPlatformToolsUri: new Uri("https://example.test/platform-tools.zip"));

        // Ensure PATH fallback doesn't short-circuit download if host lacks adb/fastboot or use isolated check
        var result = await setupSvc.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: true);

        Assert.True(result.Succeeded);
        Assert.True(settingsSvc.Current.FirstTimeSetupCompleted);
        if (!File.Exists("/usr/bin/adb") && !File.Exists("/bin/adb"))
        {
            Assert.True(result.PlatformToolsDownloaded);
            Assert.True(File.Exists(Path.Combine(workspaceDir, "platform-tools", adbName)));
            Assert.True(File.Exists(Path.Combine(workspaceDir, "platform-tools", fastbootName)));
        }
    }

    [Fact]
    public async Task InitializeEnvironmentAsync_BlocksZipSlipAndStaysOfflineResilient()
    {
        var workspaceDir = Path.Combine(_tempRoot, "workspace-offline");
        var appBaseDir = Path.Combine(_tempRoot, "appbase-offline");
        Directory.CreateDirectory(appBaseDir);

        // 1. Offline resilience: network throws HttpRequestException -> setup still succeeds cleanly!
        using var offlineHandler = new StubHttpHandler(_ => throw new HttpRequestException("No internet connection"));
        var settingsSvc = new AppSettingsService(workspaceDir);
        var offlineSetup = new FirstTimeSetupService(settingsSvc, appBaseDir, offlineHandler);

        var offlineRes = await offlineSetup.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: true);
        Assert.True(offlineRes.Succeeded);
        Assert.False(offlineRes.PlatformToolsDownloaded);
        Assert.True(settingsSvc.Current.FirstTimeSetupCompleted);

        // 2. Malicious Zip-Slip entry is blocked safely without crashing setup or escaping platform-tools/
        byte[] maliciousZip = BuildZipArchive(("../../escaped-evil.dll", "bad"));
        using var zipSlipHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(maliciousZip)
        });
        var zipSlipSetup = new FirstTimeSetupService(settingsSvc, appBaseDir, zipSlipHandler);
        var slipRes = await zipSlipSetup.InitializeEnvironmentAsync(downloadPlatformToolsIfMissing: true);

        Assert.True(slipRes.Succeeded);
        Assert.False(slipRes.PlatformToolsDownloaded);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "escaped-evil.dll")));
    }

    [Fact]
    public async Task SettingsViewModel_FirstTimeSetupCommands_UpdateBoundPathsCleanly()
    {
        var workspaceDir = Path.Combine(_tempRoot, "workspace-vm");
        var appBaseDir = Path.Combine(_tempRoot, "appbase-vm");
        Directory.CreateDirectory(Path.Combine(appBaseDir, "platform-tools"));
        var adbName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        await File.WriteAllTextAsync(Path.Combine(appBaseDir, "platform-tools", adbName), "adb-stub");

        var settingsSvc = new AppSettingsService(workspaceDir);
        var setupSvc = new FirstTimeSetupService(settingsSvc, appBaseDir);
        var vm = new SettingsViewModel(settingsSvc, setupSvc);

        await vm.RunEnvironmentCheckCommand.ExecuteAsync();

        Assert.Contains("First-time setup complete", vm.SetupReadinessSummary);
        Assert.EndsWith(adbName, vm.AdbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WpfApplicationPackagingAndXamlDictionaries_PreventSingleFileAndStaticResourceStartupFailures()
    {
        var repoRoot = FindRepoRoot();
        var appDir = Path.Combine(repoRoot, "src", "TranssionLab.App");

        // 1. Verify PublishSingleFile is false so native WPF DLLs are never extracted to %TEMP%\.net at runtime
        var csprojXml = XDocument.Load(Path.Combine(appDir, "TranssionLab.App.csproj"));
        var publishSingleFile = csprojXml.Descendants("PublishSingleFile").FirstOrDefault()?.Value;
        Assert.Equal("false", publishSingleFile);

        // 2. Collect all keys in FluentDarkTheme.xaml
        var themeRaw = File.ReadAllText(Path.Combine(appDir, "Themes", "FluentDarkTheme.xaml"));
        var definedKeys = Regex.Matches(themeRaw, @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(definedKeys);

        // 3. Verify every Views/*Templates.xaml merges FluentDarkTheme.xaml via explicit pack:// URI
        //    and every StaticResource across all XAML files exists in FluentDarkTheme.xaml
        foreach (var xamlPath in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (xamlPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(xamlPath);
            _ = XDocument.Parse(text); // Must be valid XML

            if (xamlPath.Contains($"{Path.DirectorySeparatorChar}Views{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                Assert.Contains(
                    "pack://application:,,,/TranssionLab;component/Themes/FluentDarkTheme.xaml",
                    text,
                    StringComparison.Ordinal);
            }

            foreach (Match m in Regex.Matches(text, @"\{StaticResource\s+([^\}\s,]+)"))
            {
                var key = m.Groups[1].Value;
                Assert.True(definedKeys.Contains(key), $"Missing StaticResource key '{key}' in {Path.GetFileName(xamlPath)}");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TranssionLab.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate TranssionLab.sln root.");
    }

    private static byte[] BuildZipArchive(params (string EntryPath, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
