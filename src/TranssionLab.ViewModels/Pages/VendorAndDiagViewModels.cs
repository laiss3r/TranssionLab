using System.Collections.ObjectModel;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 6: MediaTek Tools (Section 2B)
public sealed class MediaTekToolsViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly IMediaTekProvider _mtk;

    public override int PageNumber => 6;
    public override string PageTitle => "MediaTek Tools";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "Ⓜ";

    public ObservableCollection<DetectedUsbDevice> DetectedMtkEndpoints { get; } = new();

    private string _selectedComPort = string.Empty;
    public string SelectedComPort
    {
        get => _selectedComPort;
        set => SetProperty(ref _selectedComPort, value);
    }

    private MediaTekHandshakeProbeResult? _lastProbeResult;
    public MediaTekHandshakeProbeResult? LastProbeResult
    {
        get => _lastProbeResult;
        private set => SetProperty(ref _lastProbeResult, value);
    }

    public AsyncRelayCommand ScanMtkInterfacesCommand { get; }
    public AsyncRelayCommand ScanMtkEndpointsCommand => ScanMtkInterfacesCommand;
    public AsyncRelayCommand ProbeReadOnlyBromCommand { get; }
    public AsyncRelayCommand ProbeHandshakeCommand => ProbeReadOnlyBromCommand;
    public AsyncRelayCommand BootPreloaderToMetaCommand { get; }
    public AsyncRelayCommand BootPreloaderToFastbootCommand { get; }
    public AsyncRelayCommand BootPreloaderToFactoryCommand { get; }
    public RelayCommand ReleaseHeldMtkPortCommand { get; }

    public MediaTekToolsViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usb, IMediaTekProvider mtk)
    {
        _discovery = discovery;
        _usb = usb;
        _mtk = mtk;
        ScanMtkInterfacesCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshMtkAsync, "MTK USB scan failed"));
        ProbeReadOnlyBromCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(ProbeMtkWithWaitAsync, "MTK handshake probe failed"));
        BootPreloaderToMetaCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(
            () => BootPreloaderModeWithWaitAsync(PreloaderBootRequestKind.MetaModeUsb, "Boot MediaTek Preloader to META Mode"),
            "MTK Preloader META boot failed"));
        BootPreloaderToFastbootCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(
            () => BootPreloaderModeWithWaitAsync(PreloaderBootRequestKind.FastbootMode, "Boot MediaTek Preloader to Fastboot Mode"),
            "MTK Preloader Fastboot boot failed"));
        BootPreloaderToFactoryCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(
            () => BootPreloaderModeWithWaitAsync(PreloaderBootRequestKind.FactoryMode, "Boot MediaTek Preloader to Factory Test Mode"),
            "MTK Preloader Factory boot failed"));
        ReleaseHeldMtkPortCommand = new RelayCommand(() =>
        {
            var held = _mtk.HeldPortName;
            _mtk.ReleaseHeldPort();
            var msg = string.IsNullOrWhiteSpace(held) ? "No MediaTek VCOM port was currently held." : $"Released held MediaTek VCOM port {held}.";
            LogConsole($"🔓 {msg}");
            StatusBanner = msg;
        });
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshMtkAsync, "MTK USB scan failed");

    private async Task ProbeMtkWithWaitAsync()
    {
        var portToProbe = SelectedComPort?.Trim();
        var fastMtk = await _usb.EnumerateFastVcomDevicesAsync().ConfigureAwait(true);
        DetectedUsbDevice? matchedEndpoint = fastMtk.FirstOrDefault(u =>
            !string.IsNullOrWhiteSpace(portToProbe) &&
            string.Equals(u.AssociatedComPort, portToProbe, StringComparison.OrdinalIgnoreCase));

        LogPlugDeviceInstructions(
            "MediaTek BROM / Preloader Handshake",
            "Power OFF the device completely (unplug USB cable first).",
            "For Preloader VCOM: plug the USB cable directly into the phone.",
            "For BootROM (BROM): hold Vol- (or Vol+ & Vol-) while plugging the USB cable.");
        LogStep(1, 4, "Step 1/4 in progress — Waiting for USB device (MediaTek VCOM)...");

        if (matchedEndpoint is null || string.IsNullOrWhiteSpace(portToProbe))
        {
            var captured = await _discovery.WaitForVendorUsbEndpointAsync(
                "MediaTek BROM / Preloader Probe",
                u => u.DetectedChipsetVendor == ChipsetVendor.MediaTek &&
                     !string.IsNullOrWhiteSpace(u.AssociatedComPort),
                "Power OFF phone completely, hold Vol- (or Vol+ & Vol-), and plug USB cable now",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);

            matchedEndpoint = captured;
            portToProbe = captured.AssociatedComPort!;
            SelectedComPort = portToProbe;
        }

        var friendly = matchedEndpoint?.FriendlyName ?? "MediaTek USB VCOM";
        var vidPid = matchedEndpoint?.VidPidDisplay ?? "VID_0E8D&PID_2000";
        var labelWithPort = friendly.Contains(portToProbe, StringComparison.OrdinalIgnoreCase) ? friendly : $"{friendly} ({portToProbe})";
        LogConsole($"✓ USB VCOM Detected: {labelWithPort} [{vidPid}]");
        LogConsole($"Handshaking device on {portToProbe}...");
        LogStep(2, 4, $"Step 2/4 in progress — USB VCOM Detected on {portToProbe}! Handshaking device...");

        // Open COM port & send 0xA0 0x0A 0x50 0x05 IMMEDIATELY before any slow WMI refresh!
        StatusBanner = $"Handshaking device on {portToProbe} (0xA0 0x0A 0x50 0x05)...";
        LastProbeResult = await _mtk.ProbeReadOnlyBromOrPreloaderAsync(portToProbe).ConfigureAwait(true);

        if (LastProbeResult.SyncSucceeded)
        {
            LogConsole($"  • Sync 0xA0 0x0A 0x50 0x05 -> 0x5F 0xF5 0xAF 0xFA [OK]");
            LogConsole($"  • Stage    : {LastProbeResult.LoaderStageLabel ?? "BROM/Preloader"}");
            LogConsole($"  • Chipset  : {LastProbeResult.ChipsetFamilySummary} [0x{LastProbeResult.HardwareCode ?? 0:X4}]");
            LogConsole($"  • Security : SBC={LastProbeResult.SecureBootEnabled} | SLA={LastProbeResult.SerialLinkAuthRequired} | DAA={LastProbeResult.DownloadAgentAuthRequired}");
            if (!string.IsNullOrWhiteSpace(LastProbeResult.MeIdHex))
            {
                LogConsole($"  • ME_ID    : {LastProbeResult.MeIdHex}");
            }
            LogConsole($"✓ Handshake complete on {portToProbe} — VCOM held active (charging blocked).");
            LogStep(4, 4, $"Step 4/4 complete — Handshake locked {LastProbeResult.ChipsetFamilySummary} on {portToProbe}.");
        }
        else
        {
            LogConsole($"⚠ Handshake failed on {portToProbe}: {LastProbeResult.DiagnosticMessage}");
            LogStep(0, 4, LastProbeResult.DiagnosticMessage);
        }

        await RefreshMtkAsync().ConfigureAwait(true);
        StatusBanner = LastProbeResult.DiagnosticMessage;
    }

    private async Task BootPreloaderModeWithWaitAsync(PreloaderBootRequestKind targetKind, string title)
    {
        _mtk.ReleaseHeldPort();

        LogPlugDeviceInstructions(
            title,
            "Power OFF the device completely (unplug USB cable first).",
            "Do NOT hold any volume buttons (device must enter Preloader VCOM, not BROM).",
            "Plug the USB cable into the device now.");
        LogStep(1, 4, $"Step 1/4 in progress — Waiting for USB device ({title})...");

        var captured = await _discovery.WaitForVendorUsbEndpointAsync(
            title,
            u => u.DetectedChipsetVendor == ChipsetVendor.MediaTek &&
                 !string.IsNullOrWhiteSpace(u.AssociatedComPort),
            "Power OFF phone completely and plug USB cable without holding buttons",
            TimeSpan.FromSeconds(45),
            CreateWaitProgressCallback()).ConfigureAwait(true);

        var port = captured.AssociatedComPort!;
        SelectedComPort = port;
        var bootLabel = captured.FriendlyName.Contains(port, StringComparison.OrdinalIgnoreCase) ? captured.FriendlyName : $"{captured.FriendlyName} ({port})";
        LogConsole($"✓ USB VCOM Detected: {bootLabel} [{captured.VidPidDisplay}]");
        LogConsole($"Handshaking device on {port} (sending {targetKind} boot command)...");
        LogStep(2, 4, $"Step 2/4 in progress — USB VCOM Detected on {port}! Handshaking device...");

        var bootRes = await _mtk.RequestPreloaderBootModeAsync(port, targetKind).ConfigureAwait(true);
        if (bootRes.AckReceived)
        {
            LogConsole($"✓ Handshake ACK received: {bootRes.SentToken} -> {bootRes.ReceivedAckToken} [OK]");
            LogConsole($"✓ {bootRes.DiagnosticMessage}");
            LogStep(4, 4, $"Step 4/4 complete — {bootRes.DiagnosticMessage}");
        }
        else
        {
            LogConsole($"⚠ {bootRes.DiagnosticMessage}");
            LogStep(0, 4, bootRes.DiagnosticMessage);
        }

        StatusBanner = bootRes.DiagnosticMessage;
    }

    private async Task RefreshMtkAsync()
    {
        var all = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var filtered = _mtk.FilterMediaTekInterfaces(all);
        DetectedMtkEndpoints.Clear();
        foreach (var item in filtered)
        {
            DetectedMtkEndpoints.Add(item);
            if (string.IsNullOrWhiteSpace(SelectedComPort) && !string.IsNullOrWhiteSpace(item.AssociatedComPort))
                SelectedComPort = item.AssociatedComPort!;
        }
        StatusBanner = filtered.Count > 0
            ? $"Detected {filtered.Count} MediaTek USB endpoint(s) (VID 0x0E8D)."
            : "No MediaTek BROM (0E8D:0003) or Preloader (0E8D:2000) endpoint attached yet — click a button above to enter 'Waiting for USB device...' mode.";
    }
}

// Page 7: META Mode (Section 2D)
public sealed class MetaModeViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly IMetaModeProvider _meta;
    private readonly IMediaTekProvider? _mtk;

    public override int PageNumber => 7;
    public override string PageTitle => "META Mode";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "🧭";

    public ObservableCollection<MetaInterfaceDetection> DetectedMetaEndpoints { get; } = new();

    private MetaInterfaceDetection? _selectedMetaEndpoint;
    public MetaInterfaceDetection? SelectedMetaEndpoint
    {
        get => _selectedMetaEndpoint;
        set
        {
            if (SetProperty(ref _selectedMetaEndpoint, value))
                VerifyMetaSessionCommand.RaiseCanExecuteChanged();
        }
    }

    private string _providerInstallationStatus = string.Empty;
    public string ProviderInstallationStatus
    {
        get => _providerInstallationStatus;
        private set => SetProperty(ref _providerInstallationStatus, value);
    }

    private MetaSessionDiagnosticResult? _lastDiagnostic;
    public MetaSessionDiagnosticResult? LastDiagnostic
    {
        get => _lastDiagnostic;
        private set => SetProperty(ref _lastDiagnostic, value);
    }

    private string _selectedAtComPort = string.Empty;
    public string SelectedAtComPort
    {
        get => _selectedAtComPort;
        set => SetProperty(ref _selectedAtComPort, value);
    }

    private ReadOnlyModemAtInfoResult? _lastAtModemResult;
    public ReadOnlyModemAtInfoResult? LastAtModemResult
    {
        get => _lastAtModemResult;
        private set => SetProperty(ref _lastAtModemResult, value);
    }

    public AsyncRelayCommand ScanMetaEndpointsCommand { get; }
    public AsyncRelayCommand RefreshMetaEndpointsCommand => ScanMetaEndpointsCommand;
    public AsyncRelayCommand BootPhoneToMetaModeCommand { get; }
    public AsyncRelayCommand VerifyMetaSessionCommand { get; }
    public AsyncRelayCommand QueryReadOnlyAtModemCommand { get; }

    public MetaModeViewModel(
        IDeviceDiscoveryProvider discovery,
        IUsbInterfaceProvider usb,
        IMetaModeProvider meta,
        IMediaTekProvider? mtk = null)
    {
        _discovery = discovery;
        _usb = usb;
        _meta = meta;
        _mtk = mtk;
        ScanMetaEndpointsCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshMetaAsync, "META scan failed"));
        BootPhoneToMetaModeCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(BootAndHandshakeMetaModeAsync, "META Mode boot handshake failed"));
        VerifyMetaSessionCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(VerifyMetaWithWaitAsync, "META session check failed"));
        QueryReadOnlyAtModemCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(QueryReadOnlyAtModemFlowAsync, "AT Modem query failed"));
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshMetaAsync, "META scan failed");

    public Task TriggerBootAndHandshakeFromShellAsync() =>
        RunSafeActivityAsync(BootAndHandshakeMetaModeAsync, "META Mode boot handshake failed");

    private async Task BootAndHandshakeMetaModeAsync()
    {
        _mtk?.ReleaseHeldPort();

        LogPlugDeviceInstructions(
            "MediaTek META Mode — Plug Device & Handshake",
            "Power OFF the device completely (unplug USB cable first).",
            "Do NOT press any volume buttons.",
            "Plug the device into PC via USB cable now.");
        LogStep(1, 4, "Step 1/4 in progress — META Mode: Waiting for USB device...");

        var captured = await _discovery.WaitForVendorUsbEndpointAsync(
            "MediaTek META Mode VCOM Catch & Handshake",
            u => u.ClassifiedMode == ConnectionMode.MediaTekMeta ||
                 (_mtk is not null &&
                  u.DetectedChipsetVendor == ChipsetVendor.MediaTek &&
                  !string.IsNullOrWhiteSpace(u.AssociatedComPort)),
            "Power OFF phone completely and plug USB cable (no buttons)",
            TimeSpan.FromSeconds(45),
            CreateWaitProgressCallback()).ConfigureAwait(true);

        await CompleteMetaCaptureAndHandshakeAsync(captured).ConfigureAwait(true);
    }

    private async Task VerifyMetaWithWaitAsync()
    {
        if (SelectedMetaEndpoint is not null)
        {
            var ep = SelectedMetaEndpoint.UsbDevice;
            LogConsole("──────────────────────────────────────────────");
            LogConsole("▶ Operation: Verify MediaTek META Session");
            LogConsole($"✓ USB META Device Detected: {ep.FriendlyName} ({ep.AssociatedComPort ?? ep.InstanceId}) [{ep.VidPidDisplay}]");
            LogConsole($"Handshaking device ({ep.VidPidDisplay})...");
            LogStep(3, 4, $"Step 3/4 in progress — Handshaking META interface {ep.VidPidDisplay}...");

            LastDiagnostic = await _meta.DiagnoseMetaConnectionAsync(ep).ConfigureAwait(true);
            LogConsole(LastDiagnostic.SessionEstablished
                ? $"✓ {LastDiagnostic.DiagnosticSummary}"
                : $"ℹ {LastDiagnostic.DiagnosticSummary}");
            StatusBanner = LastDiagnostic.DiagnosticSummary;
            LogStep(4, 4, $"Step 4/4 complete — {LastDiagnostic.DiagnosticSummary}");
            return;
        }

        LogPlugDeviceInstructions(
            "MediaTek META Mode — Plug Device & Handshake",
            "Power OFF the device completely (unplug USB cable first).",
            "Do NOT press any volume buttons (or connect a device already in META mode).",
            "Plug the device into PC via USB cable now.");
        LogStep(1, 4, "Step 1/4 in progress — META Mode: Waiting for USB device...");

        var captured = await _discovery.WaitForVendorUsbEndpointAsync(
            "Verify MediaTek META Session",
            u => u.ClassifiedMode == ConnectionMode.MediaTekMeta ||
                 (_mtk is not null &&
                  u.DetectedChipsetVendor == ChipsetVendor.MediaTek &&
                  !string.IsNullOrWhiteSpace(u.AssociatedComPort)),
            "Power OFF phone completely and plug USB cable (or connect device in META mode)",
            TimeSpan.FromSeconds(45),
            CreateWaitProgressCallback()).ConfigureAwait(true);

        await CompleteMetaCaptureAndHandshakeAsync(captured).ConfigureAwait(true);
    }

    private async Task CompleteMetaCaptureAndHandshakeAsync(DetectedUsbDevice captured)
    {
        if (captured.ClassifiedMode != ConnectionMode.MediaTekMeta &&
            _mtk is not null &&
            !string.IsNullOrWhiteSpace(captured.AssociatedComPort))
        {
            var port = captured.AssociatedComPort!;
            var metaVcomLabel = captured.FriendlyName.Contains(port, StringComparison.OrdinalIgnoreCase) ? captured.FriendlyName : $"{captured.FriendlyName} ({port})";
            LogConsole($"✓ USB VCOM Detected: {metaVcomLabel} [{captured.VidPidDisplay}]");
            LogConsole($"Handshaking device on {port} (Sending Preloader META Mode command)...");
            LogStep(2, 4, $"Step 2/4 in progress — USB VCOM Detected on {port}! Handshaking device to META Mode...");

            var bootAck = await _mtk.RequestPreloaderBootModeAsync(port, PreloaderBootRequestKind.MetaModeUsb).ConfigureAwait(true);
            if (!bootAck.AckReceived)
            {
                LogConsole($"⚠ {bootAck.DiagnosticMessage}");
                LastDiagnostic = new MetaSessionDiagnosticResult(
                    UsbEndpointVerified: false,
                    CommunicationProviderAvailable: _meta.IsAuthorizedMetaLibraryInstalled(out _),
                    SessionEstablished: false,
                    State: ConnectionDashboardState.AuthenticationRequired,
                    DiagnosticSummary: bootAck.DiagnosticMessage,
                    Limitations: ["Preloader did not acknowledge ADVEMETA/METAMETA token."]);
                StatusBanner = bootAck.DiagnosticMessage;
                LogStep(0, 4, bootAck.DiagnosticMessage);
                return;
            }

            LogConsole($"✓ Handshake ACK received: {bootAck.SentToken} -> {bootAck.ReceivedAckToken} [OK]");
            LogConsole("  • Device accepted META Mode handshake (battery charging stopped).");
            LogConsole("Waiting for META USB interface enumeration...");
            LogStep(3, 4, "Step 3/4 in progress — Phone booting into META Mode! Waiting for META USB interface...");

            // Wait for phone to finish booting LK -> META Mode and re-enumerate USB as 0E8D:2006/2007/200A
            var metaWaitTimeout = OperatingSystem.IsWindows() ? TimeSpan.FromSeconds(18) : TimeSpan.FromMilliseconds(180);
            DetectedUsbDevice? enumeratedMeta = null;
            try
            {
                enumeratedMeta = await _discovery.WaitForVendorUsbEndpointAsync(
                    "Await META USB Enumeration",
                    u => u.ClassifiedMode == ConnectionMode.MediaTekMeta,
                    "Phone is booting into => META MODE; waiting for META USB interface...",
                    metaWaitTimeout,
                    CreateWaitProgressCallback()).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // Device booted into META screen on handset, or Windows is still binding CDC/META driver
            }

            await RefreshMetaAsync().ConfigureAwait(true);
            if (enumeratedMeta is not null || SelectedMetaEndpoint is not null)
            {
                var activeMeta = SelectedMetaEndpoint;
                var epDev = enumeratedMeta ?? activeMeta!.UsbDevice;
                LogConsole($"✓ META USB Device Detected: {epDev.FriendlyName} ({epDev.AssociatedComPort ?? epDev.InstanceId}) [{epDev.VidPidDisplay}]");
                LogConsole($"Handshaking META session on {epDev.VidPidDisplay}...");

                var diagRes = await _meta.DiagnoseMetaConnectionAsync(epDev).ConfigureAwait(true);
                var combinedMsg = $"Preloader META handshake OK ({bootAck.ReceivedAckToken}) • META USB {epDev.VidPidDisplay} enumerated. {diagRes.DiagnosticSummary}";
                LastDiagnostic = diagRes with { DiagnosticSummary = combinedMsg };
                LogConsole($"✓ {combinedMsg}");
                StatusBanner = combinedMsg;
                LogStep(4, 4, $"Step 4/4 complete — Phone in META Mode ({epDev.VidPidDisplay}).");
                return;
            }

            var bootedMsg = $"Preloader META handshake SUCCEEDED ({bootAck.SentToken} -> {bootAck.ReceivedAckToken})! Phone is booting into '=> META MODE'. Check handset screen & Device Manager for MediaTek META / ETS USB driver.";
            LastDiagnostic = new MetaSessionDiagnosticResult(
                UsbEndpointVerified: true,
                CommunicationProviderAvailable: _meta.IsAuthorizedMetaLibraryInstalled(out _),
                SessionEstablished: false,
                State: ConnectionDashboardState.DetectedButUnidentified,
                DiagnosticSummary: bootedMsg,
                Limitations: ["Handset accepted META boot token; install MediaTek META USB CDC driver if META COM port does not appear."]);
            LogConsole($"✓ {bootedMsg}");
            StatusBanner = bootedMsg;
            LogStep(4, 4, "Step 4/4 complete — Preloader META handshake succeeded (phone booting into META Mode).");
            return;
        }

        // Device was already connected in MediaTek META USB mode
        LogConsole($"✓ USB META Device Detected: {captured.FriendlyName} ({captured.AssociatedComPort ?? captured.InstanceId}) [{captured.VidPidDisplay}]");
        LogConsole($"Handshaking device ({captured.VidPidDisplay})...");
        LogStep(3, 4, $"Step 3/4 in progress — Handshaking META device {captured.VidPidDisplay}...");

        await RefreshMetaAsync().ConfigureAwait(true);
        var targetMetaUsb = SelectedMetaEndpoint?.UsbDevice ?? captured;

        StatusBanner = $"Verifying authorized META session capability on {targetMetaUsb.VidPidDisplay}...";
        LastDiagnostic = await _meta.DiagnoseMetaConnectionAsync(targetMetaUsb).ConfigureAwait(true);
        LogConsole(LastDiagnostic.SessionEstablished
            ? $"✓ {LastDiagnostic.DiagnosticSummary}"
            : $"ℹ {LastDiagnostic.DiagnosticSummary}");
        StatusBanner = LastDiagnostic.DiagnosticSummary;
        LogStep(4, 4, $"Step 4/4 complete — {LastDiagnostic.DiagnosticSummary}");
    }

    private async Task RefreshMetaAsync()
    {
        _meta.IsAuthorizedMetaLibraryInstalled(out var detail);
        ProviderInstallationStatus = detail;

        var usbList = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var detections = _meta.DetectMetaInterfaces(usbList);
        DetectedMetaEndpoints.Clear();
        foreach (var d in detections) DetectedMetaEndpoints.Add(d);
        SelectedMetaEndpoint = DetectedMetaEndpoints.FirstOrDefault();

        StatusBanner = detections.Count > 0
            ? $"Found {detections.Count} recognized META USB endpoint(s)."
            : "Click 'Boot Phone to META Mode & Handshake' — power OFF phone and plug USB when log says 'Waiting for USB device...'.";
    }

    private async Task QueryReadOnlyAtModemFlowAsync()
    {
        var port = SelectedAtComPort?.Trim();
        if (string.IsNullOrWhiteSpace(port))
        {
            port = SelectedMetaEndpoint?.UsbDevice.AssociatedComPort;
        }

        if (string.IsNullOrWhiteSpace(port))
        {
            LogPlugDeviceInstructions(
                "Read-Only Modem / AT Baseband Inquiry (ATI / CGMI / CGMM / CGMR)",
                "Connect the device in META / Factory / Modem / AT USB mode (or enter a COM port).",
                "Plug the USB cable into PC.");
            var captured = await _discovery.WaitForVendorUsbEndpointAsync(
                "Read-Only Modem AT Inquiry",
                u => !string.IsNullOrWhiteSpace(u.AssociatedComPort) &&
                     u.ClassifiedMode is not (ConnectionMode.MediaTekBrom or ConnectionMode.UnisocDownload),
                "Connect device with a Modem / AT / META / Diag USB COM port",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);
            port = captured.AssociatedComPort!;
            SelectedAtComPort = port;
            LogConsole($"✓ USB Serial Port Detected: {captured.FriendlyName} [{captured.VidPidDisplay}]");
        }
        else
        {
            LogConsole("──────────────────────────────────────────────");
            LogConsole($"▶ Operation: Read-Only Modem / AT Inquiry on {port}");
        }

        LogConsole($"Handshaking AT modem on {port} (ATI, AT+CGMI, AT+CGMM, AT+CGMR)...");
        LastAtModemResult = await _meta.QueryReadOnlyModemAtInfoAsync(port).ConfigureAwait(true);
        LogConsole(LastAtModemResult.AtAckReceived ? $"✓ {LastAtModemResult.RawSummary}" : $"⚠ {LastAtModemResult.RawSummary}");
        StatusBanner = LastAtModemResult.RawSummary;
    }
}

// Page 8: SPD / Unisoc Tools (Section 2C)
public sealed class SpdUnisocToolsViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly ISpdUnisocProvider _unisoc;

    public override int PageNumber => 8;
    public override string PageTitle => "SPD/Unisoc Tools";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "Ⓤ";

    public ObservableCollection<DetectedUsbDevice> DetectedUnisocEndpoints { get; } = new();

    private string _selectedComPort = string.Empty;
    public string SelectedComPort
    {
        get => _selectedComPort;
        set => SetProperty(ref _selectedComPort, value);
    }

    private UnisocBootProbeResult? _lastProbeResult;
    public UnisocBootProbeResult? LastProbeResult
    {
        get => _lastProbeResult;
        private set => SetProperty(ref _lastProbeResult, value);
    }

    public AsyncRelayCommand ScanUnisocEndpointsCommand { get; }
    public AsyncRelayCommand ProbeDownloadBannerCommand { get; }
    public RelayCommand ReleaseHeldUnisocPortCommand { get; }

    public SpdUnisocToolsViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usb, ISpdUnisocProvider unisoc)
    {
        _discovery = discovery;
        _usb = usb;
        _unisoc = unisoc;
        ScanUnisocEndpointsCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshUnisocAsync, "Unisoc USB scan failed"));
        ProbeDownloadBannerCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(ProbeUnisocWithWaitAsync, "Unisoc boot probe failed"));
        ReleaseHeldUnisocPortCommand = new RelayCommand(() =>
        {
            var held = _unisoc.HeldPortName;
            _unisoc.ReleaseHeldPort();
            var msg = string.IsNullOrWhiteSpace(held) ? "No Unisoc VCOM port was currently held." : $"Released held Unisoc VCOM port {held}.";
            LogConsole($"🔓 {msg}");
            StatusBanner = msg;
        });
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshUnisocAsync, "Unisoc USB scan failed");

    private async Task ProbeUnisocWithWaitAsync()
    {
        var portToProbe = SelectedComPort?.Trim();
        var fastUnisoc = await _usb.EnumerateFastVcomDevicesAsync().ConfigureAwait(true);
        DetectedUsbDevice? matchedEndpoint = fastUnisoc.FirstOrDefault(u =>
            !string.IsNullOrWhiteSpace(portToProbe) &&
            string.Equals(u.AssociatedComPort, portToProbe, StringComparison.OrdinalIgnoreCase));

        LogPlugDeviceInstructions(
            "SPD / Unisoc VCOM Download Handshake",
            "Power OFF the SPD / Unisoc device completely (unplug USB cable first).",
            "Hold Vol- (or Vol+ & Vol-) on the phone.",
            "Plug the USB cable into the device now.");
        LogStep(1, 4, "Step 1/4 in progress — Waiting for USB device (SPD / Unisoc VCOM)...");

        if (matchedEndpoint is null || string.IsNullOrWhiteSpace(portToProbe))
        {
            var captured = await _discovery.WaitForVendorUsbEndpointAsync(
                "Unisoc Boot ROM Catch & Hold (BSL_REP_VER)",
                u => u.DetectedChipsetVendor == ChipsetVendor.Unisoc &&
                     !string.IsNullOrWhiteSpace(u.AssociatedComPort),
                "Power OFF phone completely, hold Vol- / Boot key, and plug USB cable now",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);

            matchedEndpoint = captured;
            portToProbe = captured.AssociatedComPort!;
            SelectedComPort = portToProbe;
        }

        var friendly = matchedEndpoint?.FriendlyName ?? "SPRD U2S Diag";
        var vidPid = matchedEndpoint?.VidPidDisplay ?? "VID_1782&PID_4D00";
        var spdLabel = friendly.Contains(portToProbe, StringComparison.OrdinalIgnoreCase) ? friendly : $"{friendly} ({portToProbe})";
        LogConsole($"✓ USB VCOM Detected: {spdLabel} [{vidPid}]");
        LogConsole($"Handshaking device on {portToProbe} (HDLC 0x7E Check-Baud)...");
        LogStep(2, 4, $"Step 2/4 in progress — USB VCOM Detected on {portToProbe}! Handshaking device...");

        // Open COM port & send 0x7E check-baud IMMEDIATELY before any slow WMI refresh!
        StatusBanner = $"Handshaking device on {portToProbe} (HDLC 0x7E)...";
        LastProbeResult = await _unisoc.ProbeReadOnlyDownloadVersionAsync(portToProbe).ConfigureAwait(true);

        if (LastProbeResult.CheckBaudAckReceived)
        {
            LogConsole($"  • HDLC BSL_REP_VER (0x81) Banner: {LastProbeResult.BootRomBannerVersion}");
            LogConsole($"✓ Handshake complete on {portToProbe} — VCOM held active (charging blocked).");
            LogStep(4, 4, $"Step 4/4 complete — Handshake locked '{LastProbeResult.BootRomBannerVersion}' on {portToProbe}.");
        }
        else
        {
            LogConsole($"⚠ Handshake failed on {portToProbe}: {LastProbeResult.DiagnosticMessage}");
            LogStep(0, 4, LastProbeResult.DiagnosticMessage);
        }

        await RefreshUnisocAsync().ConfigureAwait(true);
        StatusBanner = LastProbeResult.DiagnosticMessage;
    }

    private async Task RefreshUnisocAsync()
    {
        var all = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var filtered = _unisoc.FilterUnisocInterfaces(all);
        DetectedUnisocEndpoints.Clear();
        foreach (var item in filtered)
        {
            DetectedUnisocEndpoints.Add(item);
            if (string.IsNullOrWhiteSpace(SelectedComPort) && !string.IsNullOrWhiteSpace(item.AssociatedComPort))
                SelectedComPort = item.AssociatedComPort!;
        }
        StatusBanner = filtered.Count > 0
            ? $"Detected {filtered.Count} Unisoc/Spreadtrum USB endpoint(s) (VID 0x1782)."
            : "No Unisoc Download (1782:4D00) endpoint attached yet — click 'Catch & Handshake Unisoc VCOM' to enter 'Waiting for USB device...' mode.";
    }
}

// Page 9: DIAG and Diagnostics (Section 2E)
public sealed class DiagDiagnosticsViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly IDiagnosticProvider _diag;

    public override int PageNumber => 9;
    public override string PageTitle => "DIAG and Diagnostics";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "∿";

    public ObservableCollection<DiagInterfaceAssessment> ClassifiedDiagEndpoints { get; } = new();

    private DiagInterfaceAssessment? _selectedEndpoint;
    public DiagInterfaceAssessment? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (SetProperty(ref _selectedEndpoint, value))
                RunReadOnlyVersionQueryCommand.RaiseCanExecuteChanged();
        }
    }

    private DiagReadOnlyQueryResult? _lastQueryResult;
    public DiagReadOnlyQueryResult? LastQueryResult
    {
        get => _lastQueryResult;
        private set => SetProperty(ref _lastQueryResult, value);
    }

    public AsyncRelayCommand ScanDiagEndpointsCommand { get; }
    public AsyncRelayCommand RunReadOnlyVersionQueryCommand { get; }

    public DiagDiagnosticsViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usb, IDiagnosticProvider diag)
    {
        _discovery = discovery;
        _usb = usb;
        _diag = diag;
        ScanDiagEndpointsCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshDiagAsync, "DIAG scan failed"));
        RunReadOnlyVersionQueryCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(QueryDiagWithWaitAsync, "DIAG version query failed"),
            () => SelectedEndpoint is null || SelectedEndpoint.SupportsReadOnlyVersionProbe);
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshDiagAsync, "DIAG scan failed");

    private async Task QueryDiagWithWaitAsync()
    {
        if (SelectedEndpoint is null)
        {
            LogPlugDeviceInstructions(
                "Read-Only DIAG Version Probe",
                "Connect the device with an active USB Diagnostic serial port enabled.",
                "Plug the USB cable into PC.");
            LogStep(1, 4, "Step 1/4 in progress — Waiting for USB DIAG device...");

            var captured = await _discovery.WaitForVendorUsbEndpointAsync(
                "Read-Only DIAG Version Probe",
                u => u.ClassifiedMode is ConnectionMode.QualcommDiag or ConnectionMode.UnisocDiag or ConnectionMode.VendorDiag or ConnectionMode.MediaTekMeta,
                "Connect device with an active diagnostic USB serial interface",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);

            LogConsole($"✓ USB DIAG Device Detected: {captured.FriendlyName} ({captured.AssociatedComPort ?? captured.InstanceId}) [{captured.VidPidDisplay}]");
            await RefreshDiagAsync().ConfigureAwait(true);
        }

        if (SelectedEndpoint is null)
            return;

        if (!SelectedEndpoint.SupportsReadOnlyVersionProbe)
        {
            LogConsole($"ℹ {SelectedEndpoint.GuardrailNotice}");
            StatusBanner = SelectedEndpoint.GuardrailNotice;
            return;
        }

        LogConsole($"Handshaking DIAG interface ({SelectedEndpoint.ProtocolFamily})...");
        StatusBanner = $"Executing read-only version query on {SelectedEndpoint.ProtocolFamily}...";
        LastQueryResult = await _diag.QueryReadOnlyDiagnosticVersionAsync(SelectedEndpoint).ConfigureAwait(true);
        LogConsole($"✓ {LastQueryResult.Summary}");
        StatusBanner = LastQueryResult.Summary;
    }

    private async Task RefreshDiagAsync()
    {
        var usbList = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var items = _diag.ClassifyDiagnosticInterfaces(usbList);
        ClassifiedDiagEndpoints.Clear();
        foreach (var i in items) ClassifiedDiagEndpoints.Add(i);
        SelectedEndpoint = ClassifiedDiagEndpoints.FirstOrDefault();

        StatusBanner = items.Count > 0
            ? $"Classified {items.Count} diagnostic endpoint(s). Protected NV/radio writes are strictly disabled."
            : "No DIAG USB interface detected. Click 'Execute Documented Read-Only DIAG Version Probe' to wait for a compatible DIAG device.";
    }
}

// Page 10: Bootloader Manager — Android 16 & Earlier (Section 5)
public sealed class BootloaderManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IBootloaderManager _bootloaderManager;

    public override int PageNumber => 10;
    public override string PageTitle => "Bootloader Manager";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "🔓";

    private BootloaderAssessment? _currentAssessment;
    public BootloaderAssessment? CurrentAssessment
    {
        get => _currentAssessment;
        private set
        {
            if (SetProperty(ref _currentAssessment, value))
            {
                ExecuteOfficialUnlockCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _confirmationInput = string.Empty;
    public string ConfirmationInput
    {
        get => _confirmationInput;
        set
        {
            if (SetProperty(ref _confirmationInput, value))
                ExecuteOfficialUnlockCommand.RaiseCanExecuteChanged();
        }
    }

    private BootloaderOperationResult? _lastResult;
    public BootloaderOperationResult? LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    public AsyncRelayCommand AssessBootloaderCommand { get; }
    public AsyncRelayCommand ExecuteOfficialUnlockCommand { get; }

    public BootloaderManagerViewModel(IDeviceDiscoveryProvider discovery, IBootloaderManager bootloaderManager)
    {
        _discovery = discovery;
        _bootloaderManager = bootloaderManager;

        AssessBootloaderCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(AssessWithWaitAsync, "Bootloader assessment failed"));
        ExecuteOfficialUnlockCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(ExecuteUnlockWithWaitAsync, "Official unlock failed"),
            () => CurrentAssessment is { IsUnlockWorkflowPermitted: true } &&
                  string.Equals(ConfirmationInput?.Trim(), CurrentAssessment.ExpectedConfirmationPhrase, StringComparison.Ordinal));

        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(() => _ = AssessIfSelectedQuietlyAsync());
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) => AssessIfSelectedQuietlyAsync();

    private async Task AssessIfSelectedQuietlyAsync()
    {
        try
        {
            if (_discovery.SelectedDevice is { } dev)
            {
                CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
                StatusBanner = CurrentAssessment.StatusSummary;
            }
            else
            {
                CurrentAssessment = null;
                StatusBanner = _discovery.RequiresExplicitSelection
                    ? "Select one device explicitly before assessing bootloader state."
                    : "Click 'Re-Assess Bootloader' to wait for a device automatically, or connect in ADB / Fastboot mode.";
            }
        }
        catch (Exception ex)
        {
            StatusBanner = ex.Message;
        }
    }

    private async Task AssessWithWaitAsync()
    {
        LogPlugDeviceInstructions(
            "Assess Bootloader Lock & Policy",
            "Connect the device in Android ADB (USB Debugging ON) or Fastboot mode.",
            "Plug the USB cable into PC.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Assess Bootloader State",
            [ConnectionMode.AdbAuthorized, ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        LogConsole($"✓ USB Device Detected: {dev.ExactModel.DisplayText} [{dev.ActiveConnectionMode}]");
        LogConsole($"Handshaking & reading bootloader state on {dev.DeviceKey}...");
        StatusBanner = $"Assessing bootloader policy and lock state on {dev.DeviceKey}...";
        CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
        LogConsole($"✓ {CurrentAssessment.StatusSummary}");
        StatusBanner = CurrentAssessment.StatusSummary;
    }

    private async Task ExecuteUnlockWithWaitAsync()
    {
        if (CurrentAssessment is null)
            return;

        LogPlugDeviceInstructions(
            "Official Fastboot Bootloader Unlock",
            "Ensure OEM Unlocking is enabled in Android Developer Options.",
            "Connect the device in Fastboot Bootloader mode.");
        var dev = await _discovery.WaitForDeviceAsync(
            "Official Bootloader Unlock",
            [ConnectionMode.FastbootBootloader],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        LogConsole($"✓ Fastboot Device Detected: {dev.TransportSerial}");
        LogConsole($"Handshaking Fastboot unlock on {dev.TransportSerial} — approve on phone screen with Volume Up...");
        StatusBanner = $"Executing official unlock on {dev.TransportSerial} — approve prompt on handset screen with Volume Up...";
        LastResult = await _bootloaderManager.ExecuteVerifiedUnlockAsync(dev, CurrentAssessment, ConfirmationInput).ConfigureAwait(true);
        LogConsole($"✓ {LastResult.Summary}");
        StatusBanner = LastResult.Summary;
        ConfirmationInput = string.Empty;
        CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
    }
}
