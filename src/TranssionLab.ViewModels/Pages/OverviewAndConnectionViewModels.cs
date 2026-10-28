using System.Collections.ObjectModel;
using System.Diagnostics;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Firmware;
using TranssionLab.Protocols.Transport;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 1: OumseGsm-Referenced Quick Servicing Hub & Unified Connection Dashboard
public sealed class DashboardViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IProtocolCapabilityService _capabilities;
    private readonly IPhoneQuickReadService? _quickRead;
    private CancellationTokenSource? _quickReadCts;

    public override int PageNumber => 1;
    public override string PageTitle => "Home & Read Info";
    public override string NavigationGroup => "OVERVIEW";
    public override string IconGlyph => "⌂";

    public Action<int>? RequestPageNavigation { get; set; }
    public Action? RequestReadInfoFocusNavigation { get; set; }
    public Action? RequestBootMetaNavigation { get; set; }
    public Action? ReleaseHeldVcomAction { get; set; }

    private bool _isReadInfoFocusMode;
    public bool IsReadInfoFocusMode
    {
        get => _isReadInfoFocusMode;
        set
        {
            if (SetProperty(ref _isReadInfoFocusMode, value))
            {
                OnPropertyChanged(nameof(IsHomeCardsVisible));
            }
        }
    }

    public bool IsHomeCardsVisible => !IsReadInfoFocusMode;

    private string _selectedBrandFilter = "ALL";
    public string SelectedBrandFilter
    {
        get => _selectedBrandFilter;
        set
        {
            if (SetProperty(ref _selectedBrandFilter, value))
            {
                OnPropertyChanged(nameof(IsBrandTecno));
                OnPropertyChanged(nameof(IsBrandInfinix));
                OnPropertyChanged(nameof(IsBrandItel));
                OnPropertyChanged(nameof(IsBrandAll));
                OnPropertyChanged(nameof(ConnectionGuidanceHeadline));
            }
        }
    }

    public bool IsBrandTecno => string.Equals(SelectedBrandFilter, "TECNO", StringComparison.OrdinalIgnoreCase);
    public bool IsBrandInfinix => string.Equals(SelectedBrandFilter, "INFINIX", StringComparison.OrdinalIgnoreCase);
    public bool IsBrandItel => string.Equals(SelectedBrandFilter, "ITEL", StringComparison.OrdinalIgnoreCase);
    public bool IsBrandAll => string.Equals(SelectedBrandFilter, "ALL", StringComparison.OrdinalIgnoreCase);

    private QuickReadTransportFilter _selectedTransportFilter = QuickReadTransportFilter.AutoDetectAll;
    public QuickReadTransportFilter SelectedTransportFilter
    {
        get => _selectedTransportFilter;
        set
        {
            if (SetProperty(ref _selectedTransportFilter, value))
            {
                OnPropertyChanged(nameof(IsModeAuto));
                OnPropertyChanged(nameof(IsModeMtk));
                OnPropertyChanged(nameof(IsModeSpd));
                OnPropertyChanged(nameof(IsModeAdb));
                OnPropertyChanged(nameof(SelectedTransportSummary));
                OnPropertyChanged(nameof(ConnectionGuidanceHeadline));
            }
        }
    }

    public bool IsModeAuto => SelectedTransportFilter == QuickReadTransportFilter.AutoDetectAll;
    public bool IsModeMtk => SelectedTransportFilter == QuickReadTransportFilter.MediaTekVcom;
    public bool IsModeSpd => SelectedTransportFilter == QuickReadTransportFilter.UnisocVcom;
    public bool IsModeAdb => SelectedTransportFilter == QuickReadTransportFilter.AdbAndFastboot;

    public string SelectedTransportSummary => SelectedTransportFilter switch
    {
        QuickReadTransportFilter.MediaTekVcom => "MTK VCOM (Preloader 0E8D:2000 / BROM 0E8D:0003)",
        QuickReadTransportFilter.UnisocVcom => "SPD / Unisoc VCOM (1782:4D00 / U2S Diag)",
        QuickReadTransportFilter.AdbAndFastboot => "ADB / Recovery / Fastboot / Fastbootd",
        _ => "Auto-Detect All (125 Hz VCOM Hold + ADB + Fastboot)"
    };

    public string ConnectionGuidanceHeadline =>
        $"Selected Brand: {(SelectedBrandFilter == "ALL" ? "All Transsion (TECNO • Infinix • itel)" : SelectedBrandFilter)}   •   Active Mode: {SelectedTransportSummary}";

    private QuickPhoneReadReport? _lastQuickReadReport;
    public QuickPhoneReadReport? LastQuickReadReport
    {
        get => _lastQuickReadReport;
        private set
        {
            if (SetProperty(ref _lastQuickReadReport, value))
            {
                OnPropertyChanged(nameof(HasQuickReadReport));
            }
        }
    }

    public bool HasQuickReadReport => LastQuickReadReport is not null;
    public ObservableCollection<KeyValuePair<string, string>> QuickReadFields { get; } = new();

    private ConnectedDeviceSnapshot? _activeDevice;
    public ConnectedDeviceSnapshot? ActiveDevice
    {
        get => _activeDevice;
        private set
        {
            if (SetProperty(ref _activeDevice, value))
            {
                OnPropertyChanged(nameof(HasActiveDevice));
                OnPropertyChanged(nameof(EmptyStateMessage));
            }
        }
    }

    public bool HasActiveDevice => ActiveDevice is not null;

    public string EmptyStateMessage => _discovery.RequiresExplicitSelection
        ? $"Multiple devices ({_discovery.ConnectedDevices.Count}) are connected. Select a target device explicitly in the top bar or Connection Manager."
        : "Step 1: Click 'READ PHONE INFO (AUTO-CATCH VCOM / USB)' above FIRST. Step 2: Power OFF the phone completely and plug the USB cable in (hold Vol- or Vol+ if needed) so TranssionLab catches VCOM before battery charging starts — OR connect a powered-ON phone with USB Debugging / Fastboot.";

    public ObservableCollection<OperationCapabilityEvaluation> AvailableOperations { get; } = new();
    public ObservableCollection<ProtocolCapabilitySummary> ProviderSummaries { get; } = new();

    public AsyncRelayCommand RefreshDashboardCommand { get; }
    public AsyncRelayCommand ReadPhoneInfoCommand { get; }
    public AsyncRelayCommand LaunchPlatformReadCommand { get; }
    public RelayCommand LaunchMetaBootFromHomeCommand { get; }
    public RelayCommand StopQuickReadCommand { get; }
    public RelayCommand CopyQuickReadReportCommand { get; }
    public RelayCommand SelectBrandCommand { get; }
    public RelayCommand SelectTransportCommand { get; }
    public RelayCommand QuickNavigateCommand { get; }

    public DashboardViewModel(
        IDeviceDiscoveryProvider discovery,
        IProtocolCapabilityService capabilities,
        IPhoneQuickReadService? quickRead = null)
    {
        _discovery = discovery;
        _capabilities = capabilities;
        _quickRead = quickRead;
        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(SyncFromDiscovery);

        RefreshDashboardCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(async () =>
        {
            StatusBanner = "Scanning USB, VCOM, ADB, and Fastboot transports...";
            ConsoleLineSink?.Invoke("▶ Scanning active USB, VCOM, ADB, and Fastboot endpoints...");
            await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
            SyncFromDiscovery();
            var msg = $"Scan complete ({_discovery.ConnectedDevices.Count} endpoint(s) detected).";
            StatusBanner = msg;
            ConsoleLineSink?.Invoke($"• {msg}");
        }, "Dashboard scan failed"));

        ReadPhoneInfoCommand = new AsyncRelayCommand(ExecuteQuickReadAsync, () => !IsBusy);
        LaunchPlatformReadCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string modeStr && Enum.TryParse<QuickReadTransportFilter>(modeStr, ignoreCase: true, out var parsed))
            {
                SelectedTransportFilter = parsed;
            }
            RequestReadInfoFocusNavigation?.Invoke();
            if (!IsBusy)
            {
                await ExecuteQuickReadAsync().ConfigureAwait(true);
            }
        }, _ => !IsBusy);
        LaunchMetaBootFromHomeCommand = new RelayCommand(() => RequestBootMetaNavigation?.Invoke());

        StopQuickReadCommand = new RelayCommand(() =>
        {
            CancelQuickRead();
            ReleaseHeldVcomAction?.Invoke();
        });

        CopyQuickReadReportCommand = new RelayCommand(() =>
        {
            if (LastQuickReadReport is null)
            {
                return;
            }

            var lines = new List<string>
            {
                $"Protocol : {LastQuickReadReport.DetectedProtocolLabel}",
                $"Endpoint : {LastQuickReadReport.PortOrSerial}",
                $"Brand    : {LastQuickReadReport.BrandSummary}",
                $"Model    : {LastQuickReadReport.ModelSummary}",
                $"Chipset  : {LastQuickReadReport.ChipsetSummary}",
                $"Version  : {LastQuickReadReport.AndroidOrBootVersion}",
                $"Security : {LastQuickReadReport.SecurityAndLockSummary}"
            };
            foreach (var kv in LastQuickReadReport.DetailedFields)
            {
                lines.Add($"{kv.Key}: {kv.Value}");
            }
            UiInteractionBridge.CopyTextToClipboard?.Invoke(string.Join(Environment.NewLine, lines));
            ConsoleLineSink?.Invoke("✓ Copied captured phone report to clipboard.");
        });

        SelectBrandCommand = new RelayCommand(param =>
        {
            if (param is string brand && !string.IsNullOrWhiteSpace(brand))
            {
                SelectedBrandFilter = brand.ToUpperInvariant();
                ConsoleLineSink?.Invoke($"• Brand focus set to: {SelectedBrandFilter}");
            }
        });

        SelectTransportCommand = new RelayCommand(param =>
        {
            if (param is string modeStr && Enum.TryParse<QuickReadTransportFilter>(modeStr, ignoreCase: true, out var parsed))
            {
                SelectedTransportFilter = parsed;
                ConsoleLineSink?.Invoke($"• Transport listener set to: {SelectedTransportSummary}");
            }
        });

        QuickNavigateCommand = new RelayCommand(param =>
        {
            if (param is string pageStr && int.TryParse(pageStr, out var pageNum))
            {
                RequestPageNavigation?.Invoke(pageNum);
            }
            else if (param is int num)
            {
                RequestPageNavigation?.Invoke(num);
            }
        });

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
            {
                ReadPhoneInfoCommand.RaiseCanExecuteChanged();
                LaunchPlatformReadCommand.RaiseCanExecuteChanged();
            }
        };

        SyncFromDiscovery();
    }

    public void ApplyCapturedReport(QuickPhoneReadReport report)
    {
        LastQuickReadReport = report;
        QuickReadFields.Clear();
        foreach (var kv in report.DetailedFields)
        {
            QuickReadFields.Add(kv);
        }
        SyncFromDiscovery();
        StatusBanner = report.GuidanceMessage;
    }

    public void CancelQuickRead()
    {
        try
        {
            _quickReadCts?.Cancel();
            _discovery.CancelActiveWait();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
    }

    private async Task ExecuteQuickReadAsync()
    {
        if (_quickRead is null)
        {
            await RefreshDashboardCommand.ExecuteAsync().ConfigureAwait(true);
            return;
        }

        await RunSafeActivityAsync(async () =>
        {
            using var cts = new CancellationTokenSource();
            _quickReadCts = cts;
            IsWaitingForDevice = true;
            try
            {
                var report = await _quickRead.CatchAndReadPhoneAsync(
                    SelectedTransportFilter,
                    SelectedBrandFilter,
                    TimeSpan.FromSeconds(60),
                    (step, total, text) =>
                    {
                        UiInteractionBridge.RunOnUiThread(() =>
                        {
                            StatusBanner = text;
                            StepProgressSink?.Invoke(step, total, text);
                        });
                    },
                    line =>
                    {
                        UiInteractionBridge.RunOnUiThread(() => ConsoleLineSink?.Invoke(line));
                    },
                    cts.Token).ConfigureAwait(true);

                ApplyCapturedReport(report);
            }
            catch (OperationCanceledException)
            {
                const string msg = "Stopped waiting for phone connection.";
                StatusBanner = msg;
                ConsoleLineSink?.Invoke($"⏹ {msg}");
                StepProgressSink?.Invoke(0, 4, "Ready — Operation cancelled by user.");
            }
            finally
            {
                _quickReadCts = null;
                IsWaitingForDevice = false;
            }
        }, "Read Phone Info failed").ConfigureAwait(true);
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        SyncFromDiscovery();
        return Task.CompletedTask;
    }

    public void SyncFromDiscovery()
    {
        ActiveDevice = _discovery.SelectedDevice;
        OnPropertyChanged(nameof(EmptyStateMessage));

        AvailableOperations.Clear();
        // Section 9: "Display only operations available for the selected device and protocol."
        foreach (var eval in _capabilities.EvaluateOperationsForDevice(ActiveDevice).Where(e => e.IsCurrentlyAvailable))
        {
            AvailableOperations.Add(eval);
        }

        ProviderSummaries.Clear();
        foreach (var summary in _capabilities.GetAllProviderSummaries())
        {
            ProviderSummaries.Add(summary);
        }
    }
}

// Page 2: Connection Manager & USB/Driver Diagnostics (Section 2G)
public sealed class ConnectionManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usbProvider;
    private readonly IProcessRunner? _processRunner;
    private readonly IAdbProvider? _adbProvider;

    public override int PageNumber => 2;
    public override string PageTitle => "Connection Manager";
    public override string NavigationGroup => "OVERVIEW";
    public override string IconGlyph => "⇄";

    public ObservableCollection<ConnectedDeviceSnapshot> DiscoveredDevices { get; } = new();
    public ObservableCollection<DetectedUsbDevice> UsbInterfaces { get; } = new();
    public ObservableCollection<ComPortDescriptor> ComPorts { get; } = new();
    public ObservableCollection<UsbConnectionHistoryEvent> ConnectionHistory { get; } = new();

    private ConnectedDeviceSnapshot? _highlightedDevice;
    public ConnectedDeviceSnapshot? HighlightedDevice
    {
        get => _highlightedDevice;
        set
        {
            if (SetProperty(ref _highlightedDevice, value))
            {
                ConfirmExplicitSelectionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _driverAuditReportText = string.Empty;
    public string DriverAuditReportText
    {
        get => _driverAuditReportText;
        private set
        {
            if (SetProperty(ref _driverAuditReportText, value))
                OnPropertyChanged(nameof(HasDriverAuditReport));
        }
    }

    public bool HasDriverAuditReport => !string.IsNullOrWhiteSpace(DriverAuditReportText);
    public bool RequiresExplicitChoice => _discovery.RequiresExplicitSelection;

    public AsyncRelayCommand ScanAllTransportsCommand { get; }
    public RelayCommand ConfirmExplicitSelectionCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand OpenDeviceManagerCommand { get; }
    public AsyncRelayCommand AuditInstalledDriversCommand { get; }
    public AsyncRelayCommand RestartAdbServerCommand { get; }

    public ConnectionManagerViewModel(
        IDeviceDiscoveryProvider discovery,
        IUsbInterfaceProvider usbProvider,
        IProcessRunner? processRunner = null,
        IAdbProvider? adbProvider = null)
    {
        _discovery = discovery;
        _usbProvider = usbProvider;
        _processRunner = processRunner;
        _adbProvider = adbProvider;

        ScanAllTransportsCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshAllCoreAsync, "Transport scan failed"));
        ConfirmExplicitSelectionCommand = new RelayCommand(
            () =>
            {
                if (HighlightedDevice is not null)
                {
                    _discovery.SelectDeviceExplicitly(HighlightedDevice.DeviceKey);
                    OnPropertyChanged(nameof(RequiresExplicitChoice));
                    StatusBanner = $"Explicitly bound session target to {HighlightedDevice.DeviceKey}.";
                    LogConsole($"✓ Bound active session target to {HighlightedDevice.DeviceKey}.");
                }
            },
            () => HighlightedDevice is not null);

        ClearSelectionCommand = new RelayCommand(() =>
        {
            _discovery.SelectDeviceExplicitly(null);
            OnPropertyChanged(nameof(RequiresExplicitChoice));
            StatusBanner = "Cleared explicit device selection.";
        });

        OpenDeviceManagerCommand = new RelayCommand(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                StatusBanner = "Windows Device Manager (devmgmt.msc) is only available on Windows hosts.";
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("mmc.exe", "devmgmt.msc") { UseShellExecute = true });
                LogConsole("▶ Opened Windows Device Manager (devmgmt.msc) — inspect Ports (COM & LPT) and Android Phone.");
                StatusBanner = "Opened Windows Device Manager (devmgmt.msc).";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Could not open Device Manager: {ex.Message}";
            }
        });

        AuditInstalledDriversCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(AuditDriverStoreAsync, "Driver store audit failed"));
        RestartAdbServerCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(async () =>
        {
            if (_adbProvider is null)
                return;
            LogConsole("▶ Restarting local ADB daemon (adb kill-server -> adb start-server)...");
            var res = await _adbProvider.RestartAdbServerAsync().ConfigureAwait(true);
            await RefreshAllCoreAsync().ConfigureAwait(true);
            var msg = res.Succeeded ? "Local ADB daemon restarted cleanly." : $"ADB restart returned warning: {res.StandardError}";
            LogConsole($"✓ {msg}");
            StatusBanner = msg;
        }, "ADB restart failed"));

        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(SyncSnapshots);
    }

    private async Task AuditDriverStoreAsync()
    {
        LogConsole("──────────────────────────────────────────────");
        LogConsole("▶ Operation: Read-Only Windows Signed USB Driver Store Audit (pnputil /enum-drivers)");
        var runner = _processRunner ?? new SystemProcessRunner();
        var res = await runner.RunAsync("pnputil.exe", ["/enum-drivers"], TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        var text = $"{res.StandardOutput}\n{res.StandardError}";

        bool hasMtk = text.Contains("mediatek", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("usb2ser", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("mtk", StringComparison.OrdinalIgnoreCase);
        bool hasSpd = text.Contains("spreadtrum", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("unisoc", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("sciu2s", StringComparison.OrdinalIgnoreCase);
        bool hasWinUsb = text.Contains("winusb", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("android_winusb", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("google", StringComparison.OrdinalIgnoreCase);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== WINDOWS SIGNED DRIVER STORE CHECK (NON-ELEVATED READ-ONLY) ===");
        sb.AppendLine($"• MediaTek USB VCOM (usb2ser / MediaTek)  : {(hasMtk ? "INSTALLED [OK]" : "NOT FOUND — Install official signed MediaTek USB VCOM driver")}");
        sb.AppendLine($"• SPD / Unisoc U2S (SciU2S / Unisoc)      : {(hasSpd ? "INSTALLED [OK]" : "NOT FOUND — Install official signed Unisoc/Spreadtrum USB driver")}");
        sb.AppendLine($"• Android ADB / Fastboot WinUSB           : {(hasWinUsb ? "INSTALLED [OK]" : "NOT FOUND — Install official Google/OEM Android WinUSB driver")}");
        DriverAuditReportText = sb.ToString().TrimEnd();
        LogConsole(DriverAuditReportText);
        StatusBanner = $"Driver Store Audit: MTK={(hasMtk ? "OK" : "Missing")}, SPD={(hasSpd ? "OK" : "Missing")}, WinUSB={(hasWinUsb ? "OK" : "Missing")}.";
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshAllCoreAsync, "Transport scan failed");

    private async Task RefreshAllCoreAsync()
    {
        StatusBanner = "Scanning USB PnP topology, COM ports, ADB, and Fastboot...";
        await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
        var usb = await _usbProvider.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var ports = await _usbProvider.EnumerateComPortsAsync().ConfigureAwait(true);

        UsbInterfaces.Clear();
        foreach (var u in usb) UsbInterfaces.Add(u);

        ComPorts.Clear();
        foreach (var p in ports) ComPorts.Add(p);

        ConnectionHistory.Clear();
        foreach (var h in _usbProvider.GetConnectionHistory().Reverse()) ConnectionHistory.Add(h);

        SyncSnapshots();
        StatusBanner = $"Enumerated {DiscoveredDevices.Count} logical device(s), {UsbInterfaces.Count} USB endpoint(s), {ComPorts.Count} COM port(s).";
    }

    private void SyncSnapshots()
    {
        DiscoveredDevices.Clear();
        foreach (var d in _discovery.ConnectedDevices)
            DiscoveredDevices.Add(d);
        HighlightedDevice = _discovery.SelectedDevice;
        OnPropertyChanged(nameof(RequiresExplicitChoice));
    }
}

public sealed record PropertyRowItem(string Key, string Value, string Provenance);

// Page 3: Device Explorer
public sealed class DeviceExplorerViewModel : PageViewModelBase
{
    private static readonly System.Text.Json.JsonSerializerOptions PrettyJsonOpts = new() { WriteIndented = true };
    private readonly IDeviceDiscoveryProvider _discovery;

    public override int PageNumber => 3;
    public override string PageTitle => "Device Explorer";
    public override string NavigationGroup => "OVERVIEW";
    public override string IconGlyph => "⊞";

    private string _propertyFilter = string.Empty;
    public string PropertyFilter
    {
        get => _propertyFilter;
        set
        {
            if (SetProperty(ref _propertyFilter, value))
                RebuildFilteredProperties();
        }
    }

    public ConnectedDeviceSnapshot? CurrentDevice => _discovery.SelectedDevice;
    public ObservableCollection<PropertyRowItem> FilteredProperties { get; } = new();

    public RelayCommand RefreshPropertiesViewCommand { get; }
    public AsyncRelayCommand ExportDeviceSnapshotJsonCommand { get; }

    private string _exportPath = string.Empty;
    public string ExportPath
    {
        get => _exportPath;
        set => SetProperty(ref _exportPath, value);
    }

    public DeviceExplorerViewModel(IDeviceDiscoveryProvider discovery, IAppSettingsService settings)
    {
        _discovery = discovery;
        _exportPath = Path.Combine(settings.Current.WorkspaceDataDirectory, "device_snapshot.json");
        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(() =>
        {
            OnPropertyChanged(nameof(CurrentDevice));
            RebuildFilteredProperties();
        });

        RefreshPropertiesViewCommand = new RelayCommand(RebuildFilteredProperties);
        ExportDeviceSnapshotJsonCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(async () =>
        {
            var dev = await _discovery.WaitForDeviceAsync(
                "Export Device Snapshot",
                acceptedModes: null,
                onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

            var fullPath = Path.GetFullPath(ExportPath);
            HashAndBinaryHelpers.EnsureParentDirectoryExists(fullPath);
            var json = System.Text.Json.JsonSerializer.Serialize(dev, PrettyJsonOpts);
            await File.WriteAllTextAsync(fullPath, json).ConfigureAwait(true);
            StatusBanner = $"Exported verified device snapshot to '{fullPath}'.";
        }, "Snapshot export failed"));
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        RebuildFilteredProperties();
        return Task.CompletedTask;
    }

    public void RebuildFilteredProperties()
    {
        FilteredProperties.Clear();
        var dev = CurrentDevice;
        if (dev is null)
        {
            StatusBanner = _discovery.RequiresExplicitSelection
                ? "Select one device explicitly to inspect properties."
                : "No active device selected. Click 'Export Snapshot JSON' to wait for a device automatically, or connect USB.";
            return;
        }

        var rows = new List<PropertyRowItem>
        {
            new("identity.manufacturer", dev.Manufacturer.DisplayText, dev.Manufacturer.BadgeText),
            new("identity.exact_model", dev.ExactModel.DisplayText, dev.ExactModel.BadgeText),
            new("identity.model_aliases", dev.ModelAliases.DisplayText, dev.ModelAliases.BadgeText),
            new("identity.codename", dev.Codename.DisplayText, dev.Codename.BadgeText),
            new("hardware.chipset_family", dev.ChipsetFamily.DisplayText, dev.ChipsetFamily.BadgeText),
            new("software.android_version", dev.AndroidVersion.DisplayText, dev.AndroidVersion.BadgeText),
            new("software.build_fingerprint", dev.BuildFingerprint.DisplayText, dev.BuildFingerprint.BadgeText),
            new("security.bootloader_state", dev.BootloaderState.DisplayText, dev.BootloaderState.BadgeText)
        };

        foreach (var (k, v) in dev.RawProperties.OrderBy(kv => kv.Key))
        {
            rows.Add(new PropertyRowItem(k, v, "DETECTED"));
        }

        var term = PropertyFilter?.Trim() ?? string.Empty;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(term) ||
                row.Key.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProperties.Add(row);
            }
        }

        StatusBanner = $"Showing {FilteredProperties.Count} property entries for {dev.DeviceKey}.";
    }
}

// Page 4: ADB Manager & Recovery Sideload (Section 2A)
public sealed class AdbManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IAdbProvider _adb;
    private readonly IRecoveryProvider _recovery;

    public override int PageNumber => 4;
    public override string PageTitle => "ADB Manager";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "⌘";

    private string _toolStatus = "Unchecked";
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }

    private string _diagnosticOutput = string.Empty;
    public string DiagnosticOutput { get => _diagnosticOutput; private set => SetProperty(ref _diagnosticOutput, value); }

    private string _otaSideloadPath = string.Empty;
    public string OtaSideloadPath
    {
        get => _otaSideloadPath;
        set
        {
            if (SetProperty(ref _otaSideloadPath, value))
                RunSideloadCommand.RaiseCanExecuteChanged();
        }
    }

    private string _apkInstallPath = string.Empty;
    public string ApkInstallPath
    {
        get => _apkInstallPath;
        set
        {
            if (SetProperty(ref _apkInstallPath, value))
                InstallApkCommand.RaiseCanExecuteChanged();
        }
    }

    private readonly List<string> _allLoadedPackages = new();
    public ObservableCollection<string> FilteredPackages { get; } = new();

    private string _packageFilter = string.Empty;
    public string PackageFilter
    {
        get => _packageFilter;
        set
        {
            if (SetProperty(ref _packageFilter, value))
                ApplyPackageFilter();
        }
    }

    private bool _thirdPartyPackagesOnly = true;
    public bool ThirdPartyPackagesOnly
    {
        get => _thirdPartyPackagesOnly;
        set => SetProperty(ref _thirdPartyPackagesOnly, value);
    }

    private string? _selectedPackage;
    public string? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (SetProperty(ref _selectedPackage, value))
                UninstallSelectedPackageCommand.RaiseCanExecuteChanged();
        }
    }

    private string _wirelessHostPort = string.Empty;
    public string WirelessHostPort
    {
        get => _wirelessHostPort;
        set
        {
            if (SetProperty(ref _wirelessHostPort, value))
            {
                PairWirelessAdbCommand.RaiseCanExecuteChanged();
                ConnectWirelessAdbCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _wirelessPairCode = string.Empty;
    public string WirelessPairCode
    {
        get => _wirelessPairCode;
        set
        {
            if (SetProperty(ref _wirelessPairCode, value))
                PairWirelessAdbCommand.RaiseCanExecuteChanged();
        }
    }

    private readonly string _capturesDir;

    public bool IsAdbAuthorizedActive => _discovery.SelectedDevice?.ActiveConnectionMode == ConnectionMode.AdbAuthorized;
    public bool IsSideloadActive => _discovery.SelectedDevice?.ActiveConnectionMode == ConnectionMode.AdbSideload;
    public bool CanRebootFromAdb => _discovery.SelectedDevice?.ActiveConnectionMode is ConnectionMode.AdbAuthorized or ConnectionMode.Recovery;

    public AsyncRelayCommand CheckAdbToolCommand { get; }
    public AsyncRelayCommand ReadBatteryAndStorageCommand { get; }
    public AsyncRelayCommand CaptureScreenshotCommand { get; }
    public AsyncRelayCommand CaptureLogcatCommand { get; }
    public RelayCommand BrowseApkCommand { get; }
    public AsyncRelayCommand InstallApkCommand { get; }
    public AsyncRelayCommand ListPackagesCommand { get; }
    public AsyncRelayCommand UninstallSelectedPackageCommand { get; }
    public AsyncRelayCommand PairWirelessAdbCommand { get; }
    public AsyncRelayCommand ConnectWirelessAdbCommand { get; }
    public AsyncRelayCommand RestartAdbDaemonCommand { get; }
    public AsyncRelayCommand RebootSystemCommand { get; }
    public AsyncRelayCommand RebootBootloaderCommand { get; }
    public AsyncRelayCommand RebootFastbootdCommand { get; }
    public AsyncRelayCommand RebootRecoveryCommand { get; }
    public AsyncRelayCommand RunSideloadCommand { get; }
    public RelayCommand BrowseOtaZipCommand { get; }

    public AdbManagerViewModel(
        IDeviceDiscoveryProvider discovery,
        IAdbProvider adb,
        IRecoveryProvider recovery,
        IAppSettingsService? settings = null)
    {
        _discovery = discovery;
        _adb = adb;
        _recovery = recovery;
        _capturesDir = Path.Combine(
            settings?.Current.WorkspaceDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TranssionLab"),
            "captures");

        BrowseOtaZipCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke("Select Signed Stock OTA ZIP", "OTA ZIP (*.zip)|*.zip|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                OtaSideloadPath = picked;
        });

        BrowseApkCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke("Select Android APK Package", "Android APK (*.apk)|*.apk|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                ApkInstallPath = picked;
        });

        CheckAdbToolCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(CheckToolAsync, "ADB binary check failed"));
        ReadBatteryAndStorageCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(CaptureHealthAsync, "ADB telemetry failed"));
        CaptureScreenshotCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(CaptureScreenshotFlowAsync, "ADB screenshot failed"));
        CaptureLogcatCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(CaptureLogcatFlowAsync, "ADB logcat failed"));
        InstallApkCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(InstallApkFlowAsync, "APK installation failed"),
            () => !string.IsNullOrWhiteSpace(ApkInstallPath));
        ListPackagesCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(ListPackagesFlowAsync, "Package enumeration failed"));
        UninstallSelectedPackageCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(UninstallPackageFlowAsync, "Package uninstall failed"),
            () => !string.IsNullOrWhiteSpace(SelectedPackage));
        PairWirelessAdbCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(PairWirelessFlowAsync, "Wireless ADB pairing failed"),
            () => !string.IsNullOrWhiteSpace(WirelessHostPort) && !string.IsNullOrWhiteSpace(WirelessPairCode));
        ConnectWirelessAdbCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(ConnectWirelessFlowAsync, "Wireless ADB connect failed"),
            () => !string.IsNullOrWhiteSpace(WirelessHostPort));
        RestartAdbDaemonCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(async () =>
        {
            LogConsole("▶ Restarting local ADB server (kill-server -> start-server)...");
            var res = await _adb.RestartAdbServerAsync().ConfigureAwait(true);
            await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
            var msg = res.Succeeded ? "ADB server restarted cleanly." : $"ADB restart error: {res.StandardError}";
            LogConsole($"✓ {msg}");
            StatusBanner = msg;
        }, "ADB restart failed"));
        RebootSystemCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootToAsync("system"), "ADB reboot failed"));
        RebootBootloaderCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootToAsync("bootloader"), "ADB reboot failed"));
        RebootFastbootdCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootToAsync("fastboot"), "ADB reboot failed"));
        RebootRecoveryCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootToAsync("recovery"), "ADB reboot failed"));
        RunSideloadCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(ExecuteSideloadAsync, "Sideload failed"),
            () => !string.IsNullOrWhiteSpace(OtaSideloadPath));

        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(RaiseModeStates);
    }

    private void ApplyPackageFilter()
    {
        var q = PackageFilter?.Trim() ?? string.Empty;
        FilteredPackages.Clear();
        foreach (var pkg in _allLoadedPackages)
        {
            if (string.IsNullOrEmpty(q) || pkg.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredPackages.Add(pkg);
            }
        }
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        RaiseModeStates();
        return RunSafeActivityAsync(CheckToolAsync, "ADB binary check failed");
    }

    private void RaiseModeStates()
    {
        OnPropertyChanged(nameof(IsAdbAuthorizedActive));
        OnPropertyChanged(nameof(IsSideloadActive));
        OnPropertyChanged(nameof(CanRebootFromAdb));
        RunSideloadCommand.RaiseCanExecuteChanged();
    }

    private async Task CheckToolAsync()
    {
        var ver = await _adb.GetToolVersionAsync().ConfigureAwait(true);
        ToolStatus = ver ?? "adb binary not found on PATH — place adb.exe in tools/ or configure path in Settings (#18).";
    }

    private async Task CaptureHealthAsync()
    {
        LogPlugDeviceInstructions(
            "ADB Read Battery & Storage Telemetry",
            "Power ON the phone and enable Developer Options -> USB Debugging.",
            "Plug the USB cable into PC and tap 'Allow' on the phone screen.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Capture Battery & Storage Telemetry",
            [ConnectionMode.AdbAuthorized],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(dev.TransportSerial))
            return;

        LogConsole($"✓ USB ADB Device Detected: {dev.ExactModel.DisplayText} ({dev.TransportSerial})");
        LogConsole($"Handshaking ADB session & reading telemetry from {dev.TransportSerial}...");
        StatusBanner = $"Reading battery & filesystem telemetry from {dev.TransportSerial}...";
        var bat = await _adb.CaptureBatterySummaryAsync(dev.TransportSerial).ConfigureAwait(true);
        var df = await _adb.CaptureStorageSummaryAsync(dev.TransportSerial).ConfigureAwait(true);
        DiagnosticOutput = $"=== BATTERY ===\n{bat}\n\n=== FILESYSTEMS ===\n{df}";
        LogConsole($"✓ Captured battery & storage telemetry from {dev.TransportSerial}.");
        StatusBanner = $"Captured read-only health telemetry from {dev.TransportSerial}.";
    }

    private async Task RebootToAsync(string mode)
    {
        LogPlugDeviceInstructions(
            $"ADB Reboot to {mode}",
            "Connect the device in Android ADB (USB Debugging ON) or Recovery mode.",
            "Plug the USB cable into PC.");
        var dev = await _discovery.WaitForDeviceAsync(
            $"ADB Reboot ({mode})",
            [ConnectionMode.AdbAuthorized, ConnectionMode.Recovery],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(dev.TransportSerial))
            return;

        LogConsole($"✓ USB ADB Device Detected: {dev.TransportSerial}");
        LogConsole($"Handshaking ADB reboot '{mode}' on {dev.TransportSerial}...");
        StatusBanner = $"Sending 'adb reboot {mode}' to {dev.TransportSerial}...";
        var res = await _adb.RebootAsync(dev.TransportSerial, mode).ConfigureAwait(true);
        if (!res.Succeeded)
        {
            LogConsole($"✗ Reboot failed: {res.StandardError}");
            StatusBanner = $"Reboot failed: {res.StandardError}";
            return;
        }

        ConnectionMode[]? postModes = mode switch
        {
            "bootloader" => [ConnectionMode.FastbootBootloader],
            "fastboot" => [ConnectionMode.FastbootdUserspace],
            "recovery" => [ConnectionMode.Recovery, ConnectionMode.AdbSideload],
            _ => null
        };

        if (postModes is not null)
        {
            try
            {
                var reenum = await _discovery.WaitForDeviceAsync(
                    $"Re-enumeration in {mode}",
                    postModes,
                    TimeSpan.FromSeconds(25),
                    CreateWaitProgressCallback()).ConfigureAwait(true);
                StatusBanner = $"Device re-enumerated in {reenum.ActiveConnectionMode} ({reenum.TransportSerial}).";
            }
            catch (Exception)
            {
                StatusBanner = $"Reboot to '{mode}' dispatched to {dev.TransportSerial}. Waiting timed out — verify USB driver if phone is on bootloader/recovery screen.";
            }
        }
        else
        {
            StatusBanner = $"Reboot to '{mode}' sent to {dev.TransportSerial}.";
        }
    }

    private async Task ExecuteSideloadAsync()
    {
        var dev = await _discovery.WaitForDeviceAsync(
            "Stream Recovery OTA Sideload",
            [ConnectionMode.AdbSideload],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(dev.TransportSerial))
            return;

        StatusBanner = $"Streaming verified OTA package '{Path.GetFileName(OtaSideloadPath)}' to {dev.TransportSerial}...";
        var res = await _recovery.VerifyAndSideloadOtaAsync(dev.TransportSerial, OtaSideloadPath).ConfigureAwait(true);
        DiagnosticOutput = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        StatusBanner = res.Succeeded ? "OTA Sideload completed." : $"Sideload failed (Exit {res.ExitCode}).";
    }

    private async Task CaptureScreenshotFlowAsync()
    {
        LogPlugDeviceInstructions(
            "ADB Device Screenshot Capture",
            "Connect powered-ON device with USB Debugging enabled.",
            "Keep phone screen unlocked on the page you want to capture.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Capture Device Screenshot",
            [ConnectionMode.AdbAuthorized],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;

        Directory.CreateDirectory(_capturesDir);
        var defaultName = $"screenshot_{HashAndBinaryHelpers.SanitizeFileNameComponent(dev.TransportSerial)}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var outPath = UiInteractionBridge.PickSaveFile?.Invoke("Save Device Screenshot PNG", "PNG Image (*.png)|*.png", defaultName)
                      ?? Path.Combine(_capturesDir, defaultName);

        LogConsole($"✓ USB ADB Device Detected: {dev.TransportSerial} — capturing lossless PNG screenshot...");
        var saved = await _adb.CaptureScreenshotAsync(dev.TransportSerial, outPath).ConfigureAwait(true);
        var msg = $"Saved lossless PNG screenshot -> '{saved}'";
        DiagnosticOutput = msg;
        LogConsole($"✓ {msg}");
        StatusBanner = msg;
    }

    private async Task CaptureLogcatFlowAsync()
    {
        LogPlugDeviceInstructions(
            "ADB Logcat & Crash Buffer Snapshot",
            "Connect powered-ON device with USB Debugging enabled.",
            "Plug USB cable into PC.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Capture Logcat Snapshot",
            [ConnectionMode.AdbAuthorized],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;

        LogConsole($"✓ USB ADB Device Detected: {dev.TransportSerial} — reading main, system & crash logcat buffers...");
        var logText = await _adb.CaptureLogcatSnapshotAsync(dev.TransportSerial, 500).ConfigureAwait(true);
        Directory.CreateDirectory(_capturesDir);
        var savedLog = Path.Combine(_capturesDir, $"logcat_{HashAndBinaryHelpers.SanitizeFileNameComponent(dev.TransportSerial)}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        await File.WriteAllTextAsync(savedLog, logText).ConfigureAwait(true);

        DiagnosticOutput = logText;
        var msg = $"Captured 500-line logcat snapshot from {dev.TransportSerial} -> '{savedLog}'";
        LogConsole($"✓ {msg}");
        StatusBanner = msg;
    }

    private async Task InstallApkFlowAsync()
    {
        LogPlugDeviceInstructions(
            "ADB APK Installer",
            "Connect powered-ON device with USB Debugging (and 'Install via USB' if prompted on HiOS/XOS).",
            "Approve any install confirmation prompt on the phone screen.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Install APK Package",
            [ConnectionMode.AdbAuthorized],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;

        LogConsole($"✓ USB ADB Device Detected: {dev.TransportSerial}");
        LogConsole($"Handshaking & installing '{Path.GetFileName(ApkInstallPath)}' on {dev.TransportSerial}...");
        var res = await _adb.InstallApkAsync(dev.TransportSerial, ApkInstallPath).ConfigureAwait(true);
        var combined = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        DiagnosticOutput = combined;
        var msg = res.Succeeded
            ? $"Installed '{Path.GetFileName(ApkInstallPath)}' on {dev.TransportSerial} [Success]."
            : $"APK install failed: {combined}";
        LogConsole(res.Succeeded ? $"✓ {msg}" : $"✗ {msg}");
        StatusBanner = msg;
    }

    private async Task ListPackagesFlowAsync()
    {
        LogPlugDeviceInstructions(
            "ADB Installed App / Package Inspector",
            "Connect powered-ON device with USB Debugging enabled.",
            "Plug USB cable into PC.");
        var dev = await _discovery.WaitForDeviceAsync(
            "List Installed Packages",
            [ConnectionMode.AdbAuthorized],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;

        LogConsole($"✓ USB ADB Device Detected: {dev.TransportSerial} — listing packages (ThirdPartyOnly={ThirdPartyPackagesOnly})...");
        var pkgs = await _adb.ListPackagesAsync(dev.TransportSerial, ThirdPartyPackagesOnly).ConfigureAwait(true);
        _allLoadedPackages.Clear();
        _allLoadedPackages.AddRange(pkgs);
        ApplyPackageFilter();
        SelectedPackage = FilteredPackages.FirstOrDefault();
        var msg = $"Loaded {pkgs.Count} package(s) from {dev.TransportSerial}.";
        LogConsole($"✓ {msg}");
        StatusBanner = msg;
    }

    private async Task UninstallPackageFlowAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPackage)) return;
        var targetPkg = SelectedPackage;
        var dev = await _discovery.WaitForDeviceAsync(
            $"Uninstall User Package ({targetPkg})",
            [ConnectionMode.AdbAuthorized],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;

        LogConsole($"▶ Uninstalling user package '{targetPkg}' on {dev.TransportSerial}...");
        var res = await _adb.UninstallUserPackageAsync(dev.TransportSerial, targetPkg).ConfigureAwait(true);
        var combined = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        DiagnosticOutput = combined;
        if (res.Succeeded)
        {
            _allLoadedPackages.Remove(targetPkg);
            ApplyPackageFilter();
            LogConsole($"✓ Uninstalled '{targetPkg}' for user 0.");
            StatusBanner = $"Uninstalled '{targetPkg}' on {dev.TransportSerial}.";
        }
        else
        {
            LogConsole($"✗ Uninstall rejected: {combined}");
            StatusBanner = $"Uninstall failed: {combined}";
        }
    }

    private async Task PairWirelessFlowAsync()
    {
        LogConsole("──────────────────────────────────────────────");
        LogConsole($"▶ Pairing Wireless ADB with {WirelessHostPort}...");
        var res = await _adb.PairWirelessAdbAsync(WirelessHostPort, WirelessPairCode).ConfigureAwait(true);
        var outStr = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        DiagnosticOutput = outStr;
        LogConsole(res.Succeeded ? $"✓ Paired with {WirelessHostPort}!" : $"✗ Pairing failed: {outStr}");
        StatusBanner = res.Succeeded ? $"Paired with {WirelessHostPort} — now enter Connect IP:Port and click Connect Wireless ADB." : $"Pair failed: {outStr}";
    }

    private async Task ConnectWirelessFlowAsync()
    {
        LogConsole("──────────────────────────────────────────────");
        LogConsole($"▶ Connecting Wireless ADB to {WirelessHostPort}...");
        var res = await _adb.ConnectWirelessAdbAsync(WirelessHostPort).ConfigureAwait(true);
        var outStr = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        DiagnosticOutput = outStr;
        await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
        LogConsole(res.Succeeded ? $"✓ Connected to {WirelessHostPort}!" : $"✗ Connect failed: {outStr}");
        StatusBanner = res.Succeeded ? $"Connected Wireless ADB to {WirelessHostPort}." : $"Wireless connect failed: {outStr}";
    }
}

// Page 5: Fastboot & Fastbootd Manager (Section 2A)
public sealed class FastbootManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IFastbootProvider _fastboot;

    public override int PageNumber => 5;
    public override string PageTitle => "Fastboot Manager";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "⚡";

    private string _toolStatus = "Unchecked";
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }

    public ObservableCollection<PropertyRowItem> FastbootVariables { get; } = new();

    public bool IsFastbootActive => _discovery.SelectedDevice?.ActiveConnectionMode is ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace;
    public bool CanModifySlot => IsFastbootActive && _discovery.SelectedDevice?.BootloaderState.Value == BootloaderLockState.Unlocked;
    public string ActiveSubModeLabel => _discovery.SelectedDevice?.ActiveConnectionMode switch
    {
        ConnectionMode.FastbootdUserspace => "Userspace Fastbootd (Logical 'super' partitions accessible)",
        ConnectionMode.FastbootBootloader => "Primary Bootloader Fastboot (Physical partitions & unlock commands)",
        _ => "Not currently in Fastboot / Fastbootd — Click any Fastboot action to wait for device automatically"
    };

    private string _tempBootImagePath = string.Empty;
    public string TempBootImagePath
    {
        get => _tempBootImagePath;
        set
        {
            if (SetProperty(ref _tempBootImagePath, value))
                BootTempImageCommand.RaiseCanExecuteChanged();
        }
    }

    private string _wipeConfirmationInput = string.Empty;
    public string WipeConfirmationInput
    {
        get => _wipeConfirmationInput;
        set
        {
            if (SetProperty(ref _wipeConfirmationInput, value))
                WipeUserdataCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand QueryVariablesCommand { get; }
    public AsyncRelayCommand SetSlotACommand { get; }
    public AsyncRelayCommand SetSlotBCommand { get; }
    public AsyncRelayCommand RebootSystemCommand { get; }
    public AsyncRelayCommand RebootBootloaderCommand { get; }
    public AsyncRelayCommand RebootFastbootdCommand { get; }
    public AsyncRelayCommand RebootRecoveryCommand { get; }
    public RelayCommand BrowseTempBootImageCommand { get; }
    public AsyncRelayCommand BootTempImageCommand { get; }
    public AsyncRelayCommand WipeUserdataCommand { get; }

    public FastbootManagerViewModel(IDeviceDiscoveryProvider discovery, IFastbootProvider fastboot)
    {
        _discovery = discovery;
        _fastboot = fastboot;

        BrowseTempBootImageCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke("Select Android Boot / Recovery Image (.img)", "Boot Image (*.img)|*.img|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                TempBootImagePath = picked;
        });

        QueryVariablesCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(QueryVarsAsync, "Fastboot variable query failed"));
        SetSlotACommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => SwitchSlotAsync("a"), "Set slot A failed"));
        SetSlotBCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => SwitchSlotAsync("b"), "Set slot B failed"));
        RebootSystemCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootAsync("system"), "Fastboot reboot failed"));
        RebootBootloaderCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootAsync("bootloader"), "Fastboot reboot failed"));
        RebootFastbootdCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootAsync("fastbootd"), "Fastbootd transition failed"));
        RebootRecoveryCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(() => RebootAsync("recovery"), "Recovery reboot failed"));
        BootTempImageCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(BootTempImageFlowAsync, "Temporary boot failed"),
            () => !string.IsNullOrWhiteSpace(TempBootImagePath));
        WipeUserdataCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(WipeUserdataFlowAsync, "Userdata factory reset failed"),
            () => string.Equals(WipeConfirmationInput?.Trim(), "WIPE USERDATA", StringComparison.Ordinal));

        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(RefreshStateBindings);
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        RefreshStateBindings();
        return RunSafeActivityAsync(async () =>
        {
            var ver = await _fastboot.GetToolVersionAsync(ct).ConfigureAwait(true);
            ToolStatus = ver ?? "fastboot binary not found on PATH — place fastboot.exe in tools/ or configure path in Settings (#18).";
        });
    }

    private void RefreshStateBindings()
    {
        OnPropertyChanged(nameof(IsFastbootActive));
        OnPropertyChanged(nameof(CanModifySlot));
        OnPropertyChanged(nameof(ActiveSubModeLabel));
    }

    private async Task QueryVarsAsync()
    {
        LogPlugDeviceInstructions(
            "Fastboot Read Variables (getvar all)",
            "Enter Fastboot or Fastbootd mode on the phone (Vol- + Power or 'adb reboot bootloader').",
            "Plug the USB cable into PC.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Query Bootloader Variables (getvar)",
            [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(dev.TransportSerial))
            return;

        LogConsole($"✓ USB Fastboot Device Detected: {dev.TransportSerial} [{dev.ActiveConnectionMode}]");
        LogConsole($"Handshaking Fastboot & reading getvar variables on {dev.TransportSerial}...");
        StatusBanner = $"Querying fastboot variables from {dev.TransportSerial}...";
        var vars = await _fastboot.GetAllVariablesAsync(dev.TransportSerial).ConfigureAwait(true);
        FastbootVariables.Clear();
        foreach (var (k, v) in vars.OrderBy(kv => kv.Key))
        {
            FastbootVariables.Add(new PropertyRowItem(k, v, "VERIFIED"));
        }
        LogConsole($"✓ Retrieved {FastbootVariables.Count} Fastboot variables from {dev.TransportSerial}.");
        StatusBanner = $"Retrieved {FastbootVariables.Count} variables from {dev.TransportSerial} ({dev.ActiveConnectionMode}).";
    }

    private async Task SwitchSlotAsync(string slot)
    {
        var dev = await _discovery.WaitForDeviceAsync(
            $"Set Active Slot ({slot.ToUpperInvariant()})",
            [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(dev.TransportSerial))
            return;

        if (dev.BootloaderState.Value != BootloaderLockState.Unlocked)
        {
            StatusBanner = "Slot switching blocked: target bootloader is Locked.";
            return;
        }

        StatusBanner = $"Switching active slot to '{slot}' on {dev.TransportSerial}...";
        var res = await _fastboot.SetActiveSlotAsync(dev.TransportSerial, slot).ConfigureAwait(true);
        var verify = await _fastboot.GetVariableAsync(dev.TransportSerial, "current-slot").ConfigureAwait(true);
        StatusBanner = res.Succeeded
            ? $"Verified active slot: current-slot={verify ?? slot}"
            : $"Slot switch rejected: {res.StandardError}";
    }

    private async Task RebootAsync(string mode)
    {
        var dev = await _discovery.WaitForDeviceAsync(
            $"Fastboot Reboot ({mode})",
            [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(dev.TransportSerial))
            return;

        StatusBanner = $"Dispatching fastboot reboot ({mode}) to {dev.TransportSerial}...";
        var res = await _fastboot.RebootAsync(dev.TransportSerial, mode).ConfigureAwait(true);
        if (!res.Succeeded)
        {
            StatusBanner = res.StandardError;
            return;
        }

        ConnectionMode[]? targetModes = mode switch
        {
            "bootloader" => [ConnectionMode.FastbootBootloader],
            "fastbootd" => [ConnectionMode.FastbootdUserspace],
            _ => null
        };

        if (targetModes is not null)
        {
            try
            {
                var reenum = await _discovery.WaitForDeviceAsync(
                    $"Re-enumeration in {mode}",
                    targetModes,
                    TimeSpan.FromSeconds(25),
                    CreateWaitProgressCallback()).ConfigureAwait(true);
                StatusBanner = $"Device ready in {reenum.ActiveConnectionMode} ({reenum.TransportSerial}).";
            }
            catch (Exception)
            {
                StatusBanner = $"Dispatched fastboot reboot ({mode}). Re-enumeration wait timed out — check device screen and WinUSB driver.";
            }
        }
        else
        {
            StatusBanner = $"Dispatched fastboot reboot ({mode}).";
        }
    }

    private async Task BootTempImageFlowAsync()
    {
        LogPlugDeviceInstructions(
            "Fastboot Temporary RAM Boot (fastboot boot)",
            "Connect an UNLOCKED device in Primary Bootloader Fastboot mode.",
            "Device will boot the selected boot/recovery image in RAM without writing flash.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Fastboot Temporary Boot Image",
            [ConnectionMode.FastbootBootloader],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;

        if (dev.BootloaderState.Value == BootloaderLockState.Locked)
        {
            const string lockedMsg = "Temporary RAM boot blocked: target bootloader is Locked. Unlock officially in Bootloader Manager (#10) first.";
            LogConsole($"✗ {lockedMsg}");
            StatusBanner = lockedMsg;
            return;
        }

        LogConsole($"✓ Fastboot Device Detected: {dev.TransportSerial} — sending '{Path.GetFileName(TempBootImagePath)}' to RAM (`fastboot boot`)...");
        var res = await _fastboot.BootTemporaryImageAsync(dev.TransportSerial, TempBootImagePath).ConfigureAwait(true);
        var msg = res.Succeeded
            ? $"Booted '{Path.GetFileName(TempBootImagePath)}' in RAM on {dev.TransportSerial}."
            : $"Temporary boot rejected: {res.StandardError}";
        LogConsole(res.Succeeded ? $"✓ {msg}" : $"✗ {msg}");
        StatusBanner = msg;
    }

    private async Task WipeUserdataFlowAsync()
    {
        LogPlugDeviceInstructions(
            "Official Unlocked Fastboot Factory Reset (Wipe Userdata)",
            "Connect an UNLOCKED device in Fastboot or Fastbootd mode.",
            "WARNING: Permanently erases userdata and metadata (never touches FRP or NVRAM).");
        var dev = await _discovery.WaitForDeviceAsync(
            "Fastboot Wipe Userdata & Metadata",
            [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;

        if (dev.BootloaderState.Value != BootloaderLockState.Unlocked)
        {
            const string lockedMsg = "Userdata format blocked: Fastboot userdata wipe requires an UNLOCKED bootloader.";
            LogConsole($"✗ {lockedMsg}");
            StatusBanner = lockedMsg;
            return;
        }

        LogConsole($"✓ Fastboot Device Detected: {dev.TransportSerial} — executing official userdata/metadata factory reset...");
        var res = await _fastboot.WipeUserdataAndMetadataAsync(dev.TransportSerial).ConfigureAwait(true);
        WipeConfirmationInput = string.Empty;
        var msg = res.Succeeded
            ? $"Completed official userdata & metadata factory reset on {dev.TransportSerial}."
            : $"Factory reset failed: {res.StandardError}";
        LogConsole(res.Succeeded ? $"✓ {msg}" : $"✗ {msg}");
        StatusBanner = msg;
    }
}
