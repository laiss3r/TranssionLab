using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Core.Services;
using TranssionLab.Firmware;
using TranssionLab.Logging;
using TranssionLab.Profiles;
using TranssionLab.Protocols.Android;
using TranssionLab.Protocols.Diagnostics;
using TranssionLab.Protocols.Discovery;
using TranssionLab.Protocols.MediaTek;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Unisoc;
using TranssionLab.Protocols.Usb;
using TranssionLab.Servicing;
using TranssionLab.ViewModels.Mvvm;
using TranssionLab.ViewModels.Pages;

namespace TranssionLab.ViewModels;

public sealed record PrimaryWorkspaceNavItem(
    string Key,
    string Icon,
    string Title,
    string Subtitle,
    IReadOnlyList<PageViewModelBase> SubPages,
    bool ReadInfoFocusOnHome = false);

public sealed class MainShellViewModel : ObservableObject, IDisposable
{
    private const int MaxConsoleLines = 200;
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IStructuredLogger _logger;
    private readonly IUsbInterfaceProvider? _usb;
    private readonly IMediaTekProvider? _mtk;
    private readonly ISpdUnisocProvider? _unisoc;
    private readonly IPhoneQuickReadService? _quickRead;
    private readonly DashboardViewModel _homeHub;
    private readonly List<string> _consoleLines = new();
    private CancellationTokenSource? _passiveWatchCts;
    private bool _syncingNav;

    // Full 19-page catalog preserved for all workflows, deep links, and headless visual self-tests
    public ObservableCollection<PageViewModelBase> Pages { get; } = new();

    // Clean OumseGsm-referenced 7-item left sidebar + horizontal sub-tab pills
    public ObservableCollection<PrimaryWorkspaceNavItem> PrimaryNavItems { get; } = new();
    public ObservableCollection<PageViewModelBase> ActiveSubTabs { get; } = new();
    public ObservableCollection<ConnectedDeviceSnapshot> ConnectedDevices { get; } = new();

    private PrimaryWorkspaceNavItem? _selectedPrimaryNav;
    public PrimaryWorkspaceNavItem? SelectedPrimaryNav
    {
        get => _selectedPrimaryNav;
        set
        {
            if (value is not null && SetProperty(ref _selectedPrimaryNav, value) && !_syncingNav)
            {
                _syncingNav = true;
                try
                {
                    RefreshSubTabsForGroup(value);
                    if (value.SubPages.Count > 0)
                    {
                        var targetPage = value.SubPages[0];
                        if (ReferenceEquals(targetPage, _homeHub))
                        {
                            _homeHub.IsReadInfoFocusMode = value.ReadInfoFocusOnHome;
                        }
                        SetCurrentPageCore(targetPage);
                    }
                }
                finally
                {
                    _syncingNav = false;
                }
            }
        }
    }

    public bool HasMultipleSubTabs => ActiveSubTabs.Count > 1;

    private PageViewModelBase _currentPage;
    public PageViewModelBase CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value is null)
                return;

            SetCurrentPageCore(value);
            if (!_syncingNav)
            {
                _syncingNav = true;
                try
                {
                    SyncPrimaryNavForPage(value);
                }
                finally
                {
                    _syncingNav = false;
                }
            }
        }
    }

    private void SetCurrentPageCore(PageViewModelBase value)
    {
        if (SetProperty(ref _currentPage, value, nameof(CurrentPage)))
        {
            _ = NavigateSafelyAsync(value);
        }
        else
        {
            OnPropertyChanged(nameof(CurrentPage));
        }
    }

    private void RefreshSubTabsForGroup(PrimaryWorkspaceNavItem group)
    {
        ActiveSubTabs.Clear();
        foreach (var sub in group.SubPages)
        {
            ActiveSubTabs.Add(sub);
        }
        OnPropertyChanged(nameof(HasMultipleSubTabs));
    }

    private void SyncPrimaryNavForPage(PageViewModelBase page)
    {
        PrimaryWorkspaceNavItem? match;
        if (ReferenceEquals(page, _homeHub))
        {
            match = _homeHub.IsReadInfoFocusMode
                ? PrimaryNavItems.FirstOrDefault(n => n.Key == "read_info")
                : PrimaryNavItems.FirstOrDefault(n => n.Key == "home");
        }
        else
        {
            match = PrimaryNavItems.FirstOrDefault(n => n.SubPages.Contains(page));
        }

        if (match is not null)
        {
            SetProperty(ref _selectedPrimaryNav, match, nameof(SelectedPrimaryNav));
            RefreshSubTabsForGroup(match);
        }
    }

    private async Task NavigateSafelyAsync(PageViewModelBase page)
    {
        try
        {
            await page.OnNavigatedToAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MainShell", $"OnNavigatedTo({page.PageTitle})", ex.Message);
            page.StatusBanner = ex.Message;
        }
    }

    private ConnectedDeviceSnapshot? _selectedDeviceItem;
    public ConnectedDeviceSnapshot? SelectedDeviceItem
    {
        get => _selectedDeviceItem;
        set
        {
            if (SetProperty(ref _selectedDeviceItem, value))
            {
                _discovery.SelectDeviceExplicitly(value?.DeviceKey);
                OnPropertyChanged(nameof(RequiresExplicitSelectionBanner));
                OnPropertyChanged(nameof(GlobalConnectionHeadline));
            }
        }
    }

    public bool RequiresExplicitSelectionBanner => _discovery.RequiresExplicitSelection;

    private bool _isAutoCatchVcomEnabled = OperatingSystem.IsWindows();
    public bool IsAutoCatchVcomEnabled
    {
        get => _isAutoCatchVcomEnabled;
        set
        {
            if (SetProperty(ref _isAutoCatchVcomEnabled, value))
            {
                OnPropertyChanged(nameof(AutoCatchButtonLabel));
                AppendConsoleLine(value
                    ? "⚡ Auto-Catch VCOM ON — Plug any powered-OFF MTK/SPD phone anytime to hold VCOM & block charging!"
                    : "⏸ Auto-Catch VCOM paused — Click 'Read Phone Info' manually when ready.");
            }
        }
    }

    public string AutoCatchButtonLabel => IsAutoCatchVcomEnabled ? "⚡ Auto-Catch: ON" : "⏸ Auto-Catch: OFF";

    private string? _heldVcomBanner;
    public string? HeldVcomBanner
    {
        get => _heldVcomBanner;
        private set
        {
            if (SetProperty(ref _heldVcomBanner, value))
            {
                OnPropertyChanged(nameof(HasHeldVcomPort));
                OnPropertyChanged(nameof(GlobalConnectionHeadline));
            }
        }
    }

    public bool HasHeldVcomPort => !string.IsNullOrWhiteSpace(HeldVcomBanner);

    private string? _waitingBannerMessage;
    public string? WaitingBannerMessage
    {
        get => _waitingBannerMessage;
        private set
        {
            if (SetProperty(ref _waitingBannerMessage, value))
            {
                OnPropertyChanged(nameof(IsWaitingForDevice));
                OnPropertyChanged(nameof(GlobalConnectionHeadline));
                CancelWaitingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsWaitingForDevice => !string.IsNullOrWhiteSpace(WaitingBannerMessage) || _homeHub.IsWaitingForDevice;

    public string GlobalConnectionHeadline
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(HeldVcomBanner))
                return HeldVcomBanner!;
            if (!string.IsNullOrWhiteSpace(WaitingBannerMessage))
                return WaitingBannerMessage!;
            if (_discovery.RequiresExplicitSelection)
                return $"AMBIGUOUS ({_discovery.ConnectedDevices.Count} devices attached — select target explicitly)";
            var sel = _discovery.SelectedDevice;
            if (sel is null)
                return IsAutoCatchVcomEnabled
                    ? "Auto-Catch VCOM Armed — Plug powered-OFF phone (holds VCOM before charging) or click Read Info"
                    : "Click 'Read Info' or select a brand/platform below to connect";
            return $"{sel.ExactModel.DisplayText} [{sel.ActiveConnectionMode}] — {sel.DashboardState}";
        }
    }

    private string _latestLogLine = "Session initialized (Offline-First Mode)";
    public string LatestLogLine
    {
        get => _latestLogLine;
        private set => SetProperty(ref _latestLogLine, value);
    }

    private string _liveConsoleText = string.Empty;
    public string LiveConsoleText
    {
        get => _liveConsoleText;
        private set => SetProperty(ref _liveConsoleText, value);
    }

    private bool _isLiveConsoleVisible = true;
    public bool IsLiveConsoleVisible
    {
        get => _isLiveConsoleVisible;
        set => SetProperty(ref _isLiveConsoleVisible, value);
    }

    private string _stepBadgeText = "READY";
    public string StepBadgeText
    {
        get => _stepBadgeText;
        private set => SetProperty(ref _stepBadgeText, value);
    }

    private string _stepDetailText = "Ready — Auto-Catch VCOM active. Plug powered-OFF phone or click '📱 Read Info'.";
    public string StepDetailText
    {
        get => _stepDetailText;
        private set => SetProperty(ref _stepDetailText, value);
    }

    private double _stepProgressPercent;
    public double StepProgressPercent
    {
        get => _stepProgressPercent;
        private set => SetProperty(ref _stepProgressPercent, value);
    }

    public AsyncRelayCommand GlobalScanDevicesCommand { get; }
    public AsyncRelayCommand GlobalReadPhoneInfoCommand { get; }
    public RelayCommand SelectSubTabCommand { get; }
    public RelayCommand NavigateToPageCommand { get; }
    public RelayCommand CancelWaitingCommand { get; }
    public RelayCommand ReleaseHeldVcomCommand { get; }
    public RelayCommand ToggleAutoCatchCommand { get; }
    public RelayCommand ToggleConsoleCommand { get; }
    public RelayCommand CopyConsoleCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }

    public MainShellViewModel(
        IDeviceDiscoveryProvider discovery,
        IStructuredLogger logger,
        DashboardViewModel p1,
        ConnectionManagerViewModel p2,
        DeviceExplorerViewModel p3,
        AdbManagerViewModel p4,
        FastbootManagerViewModel p5,
        MediaTekToolsViewModel p6,
        MetaModeViewModel p7,
        SpdUnisocToolsViewModel p8,
        DiagDiagnosticsViewModel p9,
        BootloaderManagerViewModel p10,
        FirmwareExplorerViewModel p11,
        RomStudioViewModel p12,
        PartitionManagerViewModel p13,
        FlashManagerViewModel p14,
        DeviceProfilesViewModel p15,
        OperationHistoryViewModel p16,
        LiveLogsViewModel p17,
        SettingsViewModel p18,
        DocumentationAboutViewModel p19,
        IUsbInterfaceProvider? usb = null,
        IMediaTekProvider? mtk = null,
        ISpdUnisocProvider? unisoc = null,
        IPhoneQuickReadService? quickRead = null,
        bool enableBackgroundAutoCatch = false)
    {
        _discovery = discovery;
        _logger = logger;
        _usb = usb;
        _mtk = mtk;
        _unisoc = unisoc;
        _quickRead = quickRead;
        _homeHub = p1;

        AppendConsoleLine("TranssionLab Pro v1.8.0 Ready (Offline-First • Complete Bench Toolset • Bundled ADB & Fastboot)");
        AppendConsoleLine("📋 Connection Steps: 1. Power OFF phone  2. Click Read Info / MTK / META / SPD  3. Plug USB when log says 'Waiting for USB device...'");
        AppendConsoleLine("⚡ Auto-Catch VCOM is ON — Waiting for USB device...");

        // Build full 19-page collection and wire every page into Live Console + Step Tracker
        PageViewModelBase[] allPages = [p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19];
        foreach (var page in allPages.OrderBy(p => p.PageNumber))
        {
            page.ConsoleLineSink = AppendConsoleLine;
            page.StepProgressSink = (step, total, detail) =>
            {
                UpdateStepTracker(step, total, detail);
                RefreshHeldPortBanner();
            };
            page.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PageViewModelBase.IsWaitingForDevice))
                {
                    OnPropertyChanged(nameof(IsWaitingForDevice));
                    CancelWaitingCommand?.RaiseCanExecuteChanged();
                    RefreshHeldPortBanner();
                }
            };
            Pages.Add(page);
        }

        // Build clean OumseGsm-inspired 7-section sidebar + horizontal sub-tabs
        PrimaryNavItems.Add(new PrimaryWorkspaceNavItem("home", "🏠", "Home", "TECNO • Infinix • itel", [p1], ReadInfoFocusOnHome: false));
        PrimaryNavItems.Add(new PrimaryWorkspaceNavItem("read_info", "📱", "Read Info", "VCOM Hold • ADB • Fastboot", [p1, p3], ReadInfoFocusOnHome: true));
        PrimaryNavItems.Add(new PrimaryWorkspaceNavItem("mtk", "🔧", "MTK Service", "BROM • Preloader • META", [p6, p7]));
        PrimaryNavItems.Add(new PrimaryWorkspaceNavItem("spd", "📡", "SPD / Unisoc", "U2S Boot • Diag / AT", [p8, p9]));
        PrimaryNavItems.Add(new PrimaryWorkspaceNavItem("adb", "🤖", "ADB & Fastboot", "ADB • Fastboot • Unlock", [p4, p5, p10]));
        PrimaryNavItems.Add(new PrimaryWorkspaceNavItem("firmware", "📦", "Firmware & Flash", "Partitions • Unpack • Flash", [p13, p11, p14, p12]));
        PrimaryNavItems.Add(new PrimaryWorkspaceNavItem("settings", "⚙", "Drivers & Settings", "USB Drivers • Profiles • Logs", [p2, p15, p17, p16, p18, p19]));

        _currentPage = p1;
        _selectedPrimaryNav = PrimaryNavItems[0];
        RefreshSubTabsForGroup(_selectedPrimaryNav);

        p1.RequestPageNavigation = pageNum =>
        {
            if (Pages.FirstOrDefault(x => x.PageNumber == pageNum) is { } target)
            {
                CurrentPage = target;
            }
        };
        p1.RequestReadInfoFocusNavigation = () =>
        {
            _homeHub.IsReadInfoFocusMode = true;
            SelectedPrimaryNav = PrimaryNavItems[1];
        };
        p1.RequestBootMetaNavigation = () =>
        {
            CurrentPage = p7;
            _ = p7.TriggerBootAndHandshakeFromShellAsync();
        };
        p1.ReleaseHeldVcomAction = ReleaseHeldVcomPorts;

        GlobalScanDevicesCommand = new AsyncRelayCommand(async () =>
        {
            try
            {
                UpdateStepTracker(1, 2, "Step 1/2 in progress — Scanning USB, VCOM, ADB & Fastboot...");
                CurrentPage.StatusBanner = "Scanning USB, VCOM, ADB, and Fastboot transports...";
                await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
                SyncDevicePicker();
                var count = _discovery.ConnectedDevices.Count;
                var summary = count > 0
                    ? $"Detected {count} active endpoint(s)."
                    : "0 active endpoints right now — plug powered-OFF phone or click '📱 Read Info'!";
                CurrentPage.StatusBanner = summary;
                AppendConsoleLine($"• Scan: {summary}");
                UpdateStepTracker(2, 2, summary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MainShell", "GlobalScanDevices", ex.Message);
                CurrentPage.StatusBanner = $"Scan warning: {ex.Message}";
                AppendConsoleLine($"⚠ Scan warning: {ex.Message}");
            }
        });

        GlobalReadPhoneInfoCommand = new AsyncRelayCommand(async () =>
        {
            _homeHub.IsReadInfoFocusMode = true;
            SelectedPrimaryNav = PrimaryNavItems[1];
            if (_homeHub.ReadPhoneInfoCommand.CanExecute(null))
            {
                await _homeHub.ReadPhoneInfoCommand.ExecuteAsync().ConfigureAwait(true);
            }
            RefreshHeldPortBanner();
        });

        SelectSubTabCommand = new RelayCommand(param =>
        {
            if (param is PageViewModelBase target)
            {
                if (ReferenceEquals(target, _homeHub) && SelectedPrimaryNav?.Key == "read_info")
                {
                    _homeHub.IsReadInfoFocusMode = true;
                }
                CurrentPage = target;
            }
        });

        CancelWaitingCommand = new RelayCommand(
            () =>
            {
                _homeHub.CancelQuickRead();
                _discovery.CancelActiveWait();
            },
            () => IsWaitingForDevice);

        ReleaseHeldVcomCommand = new RelayCommand(ReleaseHeldVcomPorts);
        ToggleAutoCatchCommand = new RelayCommand(() => IsAutoCatchVcomEnabled = !IsAutoCatchVcomEnabled);

        NavigateToPageCommand = new RelayCommand(param =>
        {
            if (param is PageViewModelBase target)
            {
                CurrentPage = target;
            }
            else if (param is int num && Pages.FirstOrDefault(p => p.PageNumber == num) is { } byNum)
            {
                CurrentPage = byNum;
            }
            else if (param is string numStr && int.TryParse(numStr, out var parsed) && Pages.FirstOrDefault(p => p.PageNumber == parsed) is { } byStr)
            {
                CurrentPage = byStr;
            }
        });

        ToggleConsoleCommand = new RelayCommand(() => IsLiveConsoleVisible = !IsLiveConsoleVisible);
        CopyConsoleCommand = new RelayCommand(() => UiInteractionBridge.CopyTextToClipboard?.Invoke(LiveConsoleText));
        ClearConsoleCommand = new RelayCommand(() =>
        {
            _consoleLines.Clear();
            AppendConsoleLine("Console display cleared (full audit log preserved in Logs).");
        });

        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(SyncDevicePicker);
        _discovery.WaitingStatusChanged += (_, msg) => UiInteractionBridge.RunOnUiThread(() =>
        {
            WaitingBannerMessage = msg;
            CurrentPage.IsWaitingForDevice = !string.IsNullOrWhiteSpace(msg);
            if (!string.IsNullOrWhiteSpace(msg))
            {
                CurrentPage.StatusBanner = msg;
                StepBadgeText = "WAITING";
                StepDetailText = msg!;
            }
            else if (!_homeHub.IsBusy)
            {
                StepBadgeText = "READY";
            }
        });
        _logger.EntryRecorded += (_, entry) => UiInteractionBridge.RunOnUiThread(() =>
        {
            var shortLine = $"[{entry.TimestampUtc:HH:mm:ss}] [{entry.Severity}] {entry.ProtocolProvider}: {entry.RequestedAction}";
            LatestLogLine = shortLine;
            if (entry.Severity == TranssionLab.Core.Enums.LogSeverity.Error)
            {
                AppendConsoleLineRaw(shortLine);
            }
        });

        if (enableBackgroundAutoCatch && OperatingSystem.IsWindows() && _usb is not null && _quickRead is not null)
        {
            StartPassiveWindowsVcomAutoCatch();
        }
    }

    private void ReleaseHeldVcomPorts()
    {
        var hadPort = _mtk?.HeldPortName ?? _unisoc?.HeldPortName;
        _mtk?.ReleaseHeldPort();
        _unisoc?.ReleaseHeldPort();
        HeldVcomBanner = null;
        if (!string.IsNullOrWhiteSpace(hadPort))
        {
            AppendConsoleLine($"🔓 Released held VCOM session on {hadPort}.");
        }
    }

    private void RefreshHeldPortBanner()
    {
        var mtkHold = _mtk?.HeldPortName;
        var spdHold = _unisoc?.HeldPortName;
        if (!string.IsNullOrWhiteSpace(mtkHold))
        {
            HeldVcomBanner = $"🟢 MediaTek VCOM HELD ACTIVE on {mtkHold} (Battery charging blocked)";
        }
        else if (!string.IsNullOrWhiteSpace(spdHold))
        {
            HeldVcomBanner = $"🟢 Unisoc VCOM HELD ACTIVE on {spdHold} (Battery charging blocked)";
        }
        else
        {
            HeldVcomBanner = null;
        }
    }

    /// <summary>
    /// Passive 25ms Windows SetupAPI VCOM watcher: even if the user plugs in a powered-OFF MediaTek
    /// or Unisoc phone right after opening TranssionLab WITHOUT clicking a button first, catches and
    /// locks VCOM immediately so the phone does not fall into battery charging!
    /// </summary>
    private void StartPassiveWindowsVcomAutoCatch()
    {
        _passiveWatchCts = new CancellationTokenSource();
        var token = _passiveWatchCts.Token;

        _ = Task.Run(async () =>
        {
            var cooldownByPort = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(25, token).ConfigureAwait(false);
                    RefreshHeldPortBannerOnUi();

                    if (!IsAutoCatchVcomEnabled || IsWaitingForDevice || _homeHub.IsBusy || CurrentPage.IsBusy)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(_mtk?.HeldPortName) || !string.IsNullOrWhiteSpace(_unisoc?.HeldPortName))
                    {
                        continue;
                    }

                    var fast = await _usb!.EnumerateFastVcomDevicesAsync(token).ConfigureAwait(false);
                    var transientTargets = fast
                        .Where(u => !string.IsNullOrWhiteSpace(u.AssociatedComPort) &&
                                    (u.ClassifiedMode is ConnectionMode.MediaTekBrom
                                         or ConnectionMode.MediaTekPreloader
                                         or ConnectionMode.MediaTekDownloadAgent
                                         or ConnectionMode.UnisocDownload))
                        .ToList();

                    if (transientTargets.Count != 1)
                    {
                        continue;
                    }

                    var target = transientTargets[0];
                    var port = target.AssociatedComPort!;
                    if (cooldownByPort.TryGetValue(port, out var lastTry) &&
                        (DateTimeOffset.UtcNow - lastTry) < TimeSpan.FromSeconds(4))
                    {
                        continue;
                    }

                    cooldownByPort[port] = DateTimeOffset.UtcNow;
                    var filter = target.DetectedChipsetVendor == ChipsetVendor.Unisoc
                        ? QuickReadTransportFilter.UnisocVcom
                        : QuickReadTransportFilter.MediaTekVcom;

                    UiInteractionBridge.RunOnUiThread(() =>
                    {
                        AppendConsoleLine($"⚡ Auto-Catch detected {target.FriendlyName} [{target.VidPidDisplay}] — locking VCOM immediately!");
                        _homeHub.IsReadInfoFocusMode = true;
                        SelectedPrimaryNav = PrimaryNavItems[1];
                    });

                    var report = await _quickRead!.CatchAndReadPhoneAsync(
                        filter,
                        _homeHub.SelectedBrandFilter,
                        TimeSpan.FromSeconds(4),
                        (s, t, msg) => UiInteractionBridge.RunOnUiThread(() => UpdateStepTracker(s, t, msg)),
                        line => UiInteractionBridge.RunOnUiThread(() => AppendConsoleLine(line)),
                        token).ConfigureAwait(false);

                    if (report.Succeeded)
                    {
                        UiInteractionBridge.RunOnUiThread(() =>
                        {
                            _homeHub.ApplyCapturedReport(report);
                            RefreshHeldPortBanner();
                        });
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                }
            }
        });
    }

    private void RefreshHeldPortBannerOnUi()
    {
        var current = _mtk?.HeldPortName ?? _unisoc?.HeldPortName;
        bool currentlyHas = !string.IsNullOrWhiteSpace(current);
        if (currentlyHas != HasHeldVcomPort)
        {
            UiInteractionBridge.RunOnUiThread(RefreshHeldPortBanner);
        }
    }

    public void AppendConsoleLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        AppendConsoleLineRaw($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void AppendConsoleLineRaw(string formatted)
    {
        _consoleLines.Add(formatted);
        if (_consoleLines.Count > MaxConsoleLines)
        {
            _consoleLines.RemoveRange(0, _consoleLines.Count - MaxConsoleLines);
        }
        LiveConsoleText = string.Join(Environment.NewLine, _consoleLines);
    }

    private void UpdateStepTracker(int step, int total, string detail)
    {
        if (step <= 0 || total <= 0)
        {
            StepBadgeText = "READY";
            StepProgressPercent = 0;
        }
        else if (step >= total)
        {
            StepBadgeText = $"DONE {step}/{total}";
            StepProgressPercent = 100;
        }
        else
        {
            StepBadgeText = $"STEP {step}/{total}";
            StepProgressPercent = Math.Round((double)step / total * 100.0, 0);
        }
        StepDetailText = detail;
    }

    private void SyncDevicePicker()
    {
        ConnectedDevices.Clear();
        foreach (var d in _discovery.ConnectedDevices)
            ConnectedDevices.Add(d);

        var active = _discovery.SelectedDevice;
        _selectedDeviceItem = active is null
            ? null
            : ConnectedDevices.FirstOrDefault(c => c.DeviceKey == active.DeviceKey);

        OnPropertyChanged(nameof(SelectedDeviceItem));
        OnPropertyChanged(nameof(RequiresExplicitSelectionBanner));
        OnPropertyChanged(nameof(GlobalConnectionHeadline));
    }

    public void Dispose()
    {
        try { _passiveWatchCts?.Cancel(); } catch { }
        try { _passiveWatchCts?.Dispose(); } catch { }
        _passiveWatchCts = null;
        _mtk?.ReleaseHeldPort();
        _unisoc?.ReleaseHeldPort();
    }
}

public static class ServiceRegistration
{
    public static IServiceCollection AddTranssionLabServices(
        this IServiceCollection services,
        string? customDataDirectory = null,
        IWindowsPnpBackend? customPnpBackend = null,
        IProcessRunner? customProcessRunner = null,
        ISerialChannelFactory? customSerialFactory = null)
    {
        var settingsSvc = new AppSettingsService(customDataDirectory);
        var baseDir = settingsSvc.Current.WorkspaceDataDirectory;

        services.AddSingleton<IAppSettingsService>(settingsSvc);
        services.AddSingleton<IFirstTimeSetupService>(_ => new FirstTimeSetupService(settingsSvc));
        services.AddSingleton<IStructuredLogger>(_ => new StructuredLogger(
            Path.Combine(baseDir, "logs"),
            () => settingsSvc.Current.RedactSensitiveDataInLogs));
        services.AddSingleton<IOperationHistoryStore>(_ => new OperationHistoryStore(Path.Combine(baseDir, "history")));
        services.AddSingleton<IDeviceProfileRepository>(_ => new DeviceProfileRepository(Path.Combine(baseDir, "profiles")));
        services.AddSingleton<IOperationRegistry, OperationRegistry>();

        services.AddSingleton<IWindowsPnpBackend>(_ => customPnpBackend ?? new WindowsWmiPnpBackend());
        services.AddSingleton<IProcessRunner>(_ => customProcessRunner ?? new SystemProcessRunner());
        services.AddSingleton<ISerialChannelFactory>(_ => customSerialFactory ?? new SystemSerialChannelFactory());

        services.AddSingleton<IUsbInterfaceProvider, UsbInterfaceProvider>();
        services.AddSingleton<IAdbProvider, AdbProvider>();
        services.AddSingleton<IRecoveryProvider, RecoveryProvider>();
        services.AddSingleton<IFastbootProvider, FastbootProvider>();
        services.AddSingleton<IMediaTekProvider, MediaTekProvider>();
        services.AddSingleton<IMetaModeProvider, MetaModeProvider>();
        services.AddSingleton<ISpdUnisocProvider, SpdUnisocProvider>();
        services.AddSingleton<IDiagnosticProvider, DiagnosticProvider>();
        services.AddSingleton<IProtocolCapabilityService, ProtocolCapabilityService>();
        services.AddSingleton<IDeviceDiscoveryProvider, DeviceDiscoveryProvider>();
        services.AddSingleton<IPhoneQuickReadService, PhoneQuickReadService>();

        services.AddSingleton<IFirmwareFormatRegistry, FirmwareFormatRegistry>();
        services.AddSingleton<RomStudioService>();
        services.AddSingleton<IBootloaderManager, BootloaderManager>();
        services.AddSingleton<IFlashManager, FlashManager>();
        services.AddSingleton<IPartitionManager, PartitionManager>();

        // Register all 19 Page ViewModels + Shell
        services.AddSingleton<DashboardViewModel>(sp => new DashboardViewModel(
            sp.GetRequiredService<IDeviceDiscoveryProvider>(),
            sp.GetRequiredService<IProtocolCapabilityService>(),
            sp.GetRequiredService<IPhoneQuickReadService>()));
        services.AddSingleton<ConnectionManagerViewModel>();
        services.AddSingleton<DeviceExplorerViewModel>();
        services.AddSingleton<AdbManagerViewModel>();
        services.AddSingleton<FastbootManagerViewModel>();
        services.AddSingleton<MediaTekToolsViewModel>();
        services.AddSingleton<MetaModeViewModel>(sp => new MetaModeViewModel(
            sp.GetRequiredService<IDeviceDiscoveryProvider>(),
            sp.GetRequiredService<IUsbInterfaceProvider>(),
            sp.GetRequiredService<IMetaModeProvider>(),
            sp.GetRequiredService<IMediaTekProvider>()));
        services.AddSingleton<SpdUnisocToolsViewModel>();
        services.AddSingleton<DiagDiagnosticsViewModel>();
        services.AddSingleton<BootloaderManagerViewModel>();
        services.AddSingleton<FlashManagerViewModel>();
        services.AddSingleton<FirmwareExplorerViewModel>();
        services.AddSingleton<RomStudioViewModel>();
        services.AddSingleton<PartitionManagerViewModel>();
        services.AddSingleton<DeviceProfilesViewModel>();
        services.AddSingleton<OperationHistoryViewModel>();
        services.AddSingleton<LiveLogsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DocumentationAboutViewModel>();
        bool isLiveHardwareApp = customPnpBackend is null && customSerialFactory is null;
        services.AddSingleton<MainShellViewModel>(sp => new MainShellViewModel(
            sp.GetRequiredService<IDeviceDiscoveryProvider>(),
            sp.GetRequiredService<IStructuredLogger>(),
            sp.GetRequiredService<DashboardViewModel>(),
            sp.GetRequiredService<ConnectionManagerViewModel>(),
            sp.GetRequiredService<DeviceExplorerViewModel>(),
            sp.GetRequiredService<AdbManagerViewModel>(),
            sp.GetRequiredService<FastbootManagerViewModel>(),
            sp.GetRequiredService<MediaTekToolsViewModel>(),
            sp.GetRequiredService<MetaModeViewModel>(),
            sp.GetRequiredService<SpdUnisocToolsViewModel>(),
            sp.GetRequiredService<DiagDiagnosticsViewModel>(),
            sp.GetRequiredService<BootloaderManagerViewModel>(),
            sp.GetRequiredService<FirmwareExplorerViewModel>(),
            sp.GetRequiredService<RomStudioViewModel>(),
            sp.GetRequiredService<PartitionManagerViewModel>(),
            sp.GetRequiredService<FlashManagerViewModel>(),
            sp.GetRequiredService<DeviceProfilesViewModel>(),
            sp.GetRequiredService<OperationHistoryViewModel>(),
            sp.GetRequiredService<LiveLogsViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<DocumentationAboutViewModel>(),
            sp.GetRequiredService<IUsbInterfaceProvider>(),
            sp.GetRequiredService<IMediaTekProvider>(),
            sp.GetRequiredService<ISpdUnisocProvider>(),
            sp.GetRequiredService<IPhoneQuickReadService>(),
            enableBackgroundAutoCatch: isLiveHardwareApp));

        return services;
    }
}
