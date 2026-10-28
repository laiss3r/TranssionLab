using System.Diagnostics;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Protocols.Discovery;

/// <summary>
/// High-speed 125 Hz (8ms) non-blocking VCOM/WinUSB Catch-and-Hold + Parallel ADB/Fastboot Phone Reader:
/// - Runs VCOM/WinUSB polling on its own dedicated 8ms loop (zero adb.exe or WMI blocking!).
/// - Catches MediaTek Preloader/BROM (0E8D), Unisoc Download (1782 / SPRD_ENUM), WinUSB bulk pipes,
///   and newly arrived live COMx ports within milliseconds of USB plug-in so the phone is locked
///   before Preloader's ~1.8s timer drops into battery charging.
/// - Simultaneously runs ADB &amp; Fastboot inspection on an isolated parallel task.
/// </summary>
public sealed class PhoneQuickReadService : IPhoneQuickReadService
{
    private readonly IUsbInterfaceProvider _usb;
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IAdbProvider _adb;
    private readonly IFastbootProvider _fastboot;
    private readonly IMediaTekProvider _mtk;
    private readonly ISpdUnisocProvider _unisoc;
    private readonly IDeviceProfileRepository _profiles;
    private readonly IStructuredLogger _logger;

    public TimeSpan FastVcomPollDelay { get; set; } = TimeSpan.FromMilliseconds(8);

    public PhoneQuickReadService(
        IUsbInterfaceProvider usb,
        IDeviceDiscoveryProvider discovery,
        IAdbProvider adb,
        IFastbootProvider fastboot,
        IMediaTekProvider mtk,
        ISpdUnisocProvider unisoc,
        IDeviceProfileRepository profiles,
        IStructuredLogger logger)
    {
        _usb = usb;
        _discovery = discovery;
        _adb = adb;
        _fastboot = fastboot;
        _mtk = mtk;
        _unisoc = unisoc;
        _profiles = profiles;
        _logger = logger;
    }

    public async Task<QuickPhoneReadReport> CatchAndReadPhoneAsync(
        QuickReadTransportFilter filter = QuickReadTransportFilter.AutoDetectAll,
        string? preferredBrandHint = null,
        TimeSpan? timeout = null,
        Action<int, int, string>? onStepProgress = null,
        Action<string>? onConsoleLine = null,
        CancellationToken ct = default)
    {
        var maxWait = timeout ?? TimeSpan.FromSeconds(60);
        var brandTag = string.IsNullOrWhiteSpace(preferredBrandHint) || preferredBrandHint == "ALL"
            ? "TECNO / Infinix / itel"
            : preferredBrandHint;

        // Release any previously held VCOM port before starting a fresh read
        _mtk.ReleaseHeldPort();
        _unisoc.ReleaseHeldPort();

        onStepProgress?.Invoke(1, 4, $"Step 1/4 in progress — Waiting for USB device ({brandTag} • {filter})...");
        onConsoleLine?.Invoke("──────────────────────────────────────────────");
        onConsoleLine?.Invoke($"▶ Operation: Read Phone Info [{brandTag} • Mode: {filter}]");
        onConsoleLine?.Invoke("📋 Instructions:");

        bool runVcom = filter is QuickReadTransportFilter.AutoDetectAll or QuickReadTransportFilter.MediaTekVcom or QuickReadTransportFilter.UnisocVcom;
        bool runAdbFastboot = filter is QuickReadTransportFilter.AutoDetectAll or QuickReadTransportFilter.AdbAndFastboot;

        if (runVcom && runAdbFastboot)
        {
            onConsoleLine?.Invoke("  1. Power OFF the device completely (unplug USB cable first).");
            onConsoleLine?.Invoke("  2. Plug the device into PC via USB cable (hold Vol- or Vol+ & Vol- if needed).");
            onConsoleLine?.Invoke("  3. Or connect powered-ON device in ADB (USB Debugging) / Fastboot mode.");
        }
        else if (filter == QuickReadTransportFilter.MediaTekVcom)
        {
            onConsoleLine?.Invoke("  1. Power OFF the MediaTek device completely (unplug USB cable first).");
            onConsoleLine?.Invoke("  2. Plug the device into PC via USB cable (hold Vol- or Vol+ & Vol- for BROM).");
        }
        else if (filter == QuickReadTransportFilter.UnisocVcom)
        {
            onConsoleLine?.Invoke("  1. Power OFF the SPD / Unisoc device completely (unplug USB cable first).");
            onConsoleLine?.Invoke("  2. Hold Vol- (or Vol+ & Vol-) and plug the device into PC via USB cable.");
        }
        else
        {
            onConsoleLine?.Invoke("  1. Connect the device in Android ADB (USB Debugging ON) or Fastboot mode.");
            onConsoleLine?.Invoke("  2. Accept the 'Allow USB debugging' prompt on the phone screen if asked.");
        }

        onConsoleLine?.Invoke("Waiting for USB device...");

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        raceCts.CancelAfter(maxWait);

        var activeTasks = new List<Task<QuickPhoneReadReport?>>();
        if (runVcom)
        {
            activeTasks.Add(RunDedicatedVcomCatchLoopAsync(filter, brandTag, maxWait, onStepProgress, onConsoleLine, raceCts.Token));
        }
        if (runAdbFastboot)
        {
            activeTasks.Add(RunParallelAdbAndFastbootLoopAsync(maxWait, onStepProgress, onConsoleLine, raceCts.Token));
        }

        try
        {
            while (activeTasks.Count > 0)
            {
                var finished = await Task.WhenAny(activeTasks).ConfigureAwait(false);
                activeTasks.Remove(finished);

                var report = await finished.ConfigureAwait(false);
                if (report is not null)
                {
                    try { await raceCts.CancelAsync().ConfigureAwait(false); } catch { }
                    return report;
                }
            }
        }
        catch (AmbiguousDeviceSelectionException)
        {
            try { await raceCts.CancelAsync().ConfigureAwait(false); } catch { }
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout window elapsed cleanly
        }

        var timeoutMsg = $"Timed out after {(int)maxWait.TotalSeconds}s waiting for VCOM, ADB, or Fastboot. If the phone only charges: (1) ensure MediaTek / Unisoc USB VCOM driver is installed in Device Manager, (2) power OFF the phone completely, and (3) hold Vol- (or Vol+ & Vol-) while plugging USB.";
        _logger.LogWarning("PhoneQuickRead", "CatchAndReadTimeout", timeoutMsg);
        onConsoleLine?.Invoke($"✗ {timeoutMsg}");
        return new QuickPhoneReadReport(
            Succeeded: false,
            DetectedProtocolLabel: "No Readable Interface Caught",
            PortOrSerial: "—",
            BrandSummary: "—",
            ModelSummary: "Timed out waiting for VCOM / ADB / Fastboot",
            ModelProvenance: DataProvenance.Unknown,
            ChipsetSummary: "—",
            AndroidOrBootVersion: "—",
            SecurityAndLockSummary: "—",
            SlotAndStorageSummary: "—",
            GuidanceMessage: timeoutMsg,
            DetailedFields: Array.Empty<KeyValuePair<string, string>>());
    }

    private async Task<QuickPhoneReadReport?> RunDedicatedVcomCatchLoopAsync(
        QuickReadTransportFilter filter,
        string brandTag,
        TimeSpan maxWait,
        Action<int, int, string>? onStepProgress,
        Action<string>? onConsoleLine,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var reportedDriverProblemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baselineGenericPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recentlyFailedPorts = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        // Snapshot non-MTK/Unisoc generic COM ports present before the user plugs in the phone
        var initialFast = await _usb.EnumerateFastVcomDevicesAsync(ct).ConfigureAwait(false);
        foreach (var d in initialFast)
        {
            if (!string.IsNullOrWhiteSpace(d.AssociatedComPort) &&
                d.DetectedChipsetVendor is not (ChipsetVendor.MediaTek or ChipsetVendor.Unisoc))
            {
                baselineGenericPorts.Add(d.AssociatedComPort!);
            }
        }

        int lastReportedSec = 0;

        while (!ct.IsCancellationRequested && sw.Elapsed < maxWait)
        {
            var fastUsb = await _usb.EnumerateFastVcomDevicesAsync(ct).ConfigureAwait(false);

            // 1. Report transient Preloader/Unisoc driver problems (Code 28 / Code 52 / Code 10) in <1ms!
            foreach (var prob in fastUsb)
            {
                if (prob.DriverDiagnostics.Status != DriverHealthStatus.Healthy &&
                    reportedDriverProblemIds.Add(prob.InstanceId))
                {
                    onConsoleLine?.Invoke($"⚠ Caught {prob.FriendlyName} [{prob.VidPidDisplay}] on USB, but Windows Driver failed: {prob.DriverDiagnostics.Summary}");
                    onConsoleLine?.Invoke($"  👉 Fix: {prob.DriverDiagnostics.SafeRemediationGuidance}");
                }
            }

            // 2. Check MediaTek VCOM / WinUSB endpoints
            if (filter is QuickReadTransportFilter.AutoDetectAll or QuickReadTransportFilter.MediaTekVcom)
            {
                var mtkPorts = fastUsb
                    .Where(u => u.DetectedChipsetVendor == ChipsetVendor.MediaTek &&
                                !string.IsNullOrWhiteSpace(u.AssociatedComPort) &&
                                u.ClassifiedMode is ConnectionMode.MediaTekBrom
                                    or ConnectionMode.MediaTekPreloader
                                    or ConnectionMode.MediaTekDownloadAgent)
                    .ToList();

                if (mtkPorts.Count > 1)
                {
                    throw new AmbiguousDeviceSelectionException(mtkPorts.Count);
                }

                if (mtkPorts.Count == 1)
                {
                    var dev = mtkPorts[0];
                    var port = dev.AssociatedComPort!;
                    if (!IsInCooldown(recentlyFailedPorts, port))
                    {
                        var report = await CatchMediaTekEndpointAsync(dev, port, brandTag, sw.Elapsed, onStepProgress, onConsoleLine, ct).ConfigureAwait(false);
                        if (report is not null)
                        {
                            return report;
                        }
                        recentlyFailedPorts[port] = DateTimeOffset.UtcNow;
                    }
                }
            }

            // 3. Check Unisoc / Spreadtrum VCOM / WinUSB endpoints
            if (filter is QuickReadTransportFilter.AutoDetectAll or QuickReadTransportFilter.UnisocVcom)
            {
                var spdPorts = fastUsb
                    .Where(u => u.DetectedChipsetVendor == ChipsetVendor.Unisoc &&
                                !string.IsNullOrWhiteSpace(u.AssociatedComPort))
                    .ToList();

                if (spdPorts.Count > 1)
                {
                    throw new AmbiguousDeviceSelectionException(spdPorts.Count);
                }

                if (spdPorts.Count == 1)
                {
                    var dev = spdPorts[0];
                    var port = dev.AssociatedComPort!;
                    if (!IsInCooldown(recentlyFailedPorts, port))
                    {
                        var report = await CatchUnisocEndpointAsync(dev, port, brandTag, sw.Elapsed, onStepProgress, onConsoleLine, ct).ConfigureAwait(false);
                        if (report is not null)
                        {
                            return report;
                        }
                        recentlyFailedPorts[port] = DateTimeOffset.UtcNow;
                    }
                }
            }

            // 4. Differential catch for any brand-new generic USB COM port that appeared AFTER listening started!
            var newlyArrivedGeneric = fastUsb
                .Where(u => !string.IsNullOrWhiteSpace(u.AssociatedComPort) &&
                            u.DetectedChipsetVendor is not (ChipsetVendor.MediaTek or ChipsetVendor.Unisoc) &&
                            !baselineGenericPorts.Contains(u.AssociatedComPort!))
                .ToList();

            if (newlyArrivedGeneric.Count == 1)
            {
                var dev = newlyArrivedGeneric[0];
                var port = dev.AssociatedComPort!;
                if (!IsInCooldown(recentlyFailedPorts, port))
                {
                    if (filter is QuickReadTransportFilter.AutoDetectAll or QuickReadTransportFilter.MediaTekVcom)
                    {
                        var mtkTry = await CatchMediaTekEndpointAsync(dev, port, brandTag, sw.Elapsed, onStepProgress, onConsoleLine, ct).ConfigureAwait(false);
                        if (mtkTry is not null)
                        {
                            return mtkTry;
                        }
                    }

                    if (filter is QuickReadTransportFilter.AutoDetectAll or QuickReadTransportFilter.UnisocVcom)
                    {
                        var spdTry = await CatchUnisocEndpointAsync(dev, port, brandTag, sw.Elapsed, onStepProgress, onConsoleLine, ct).ConfigureAwait(false);
                        if (spdTry is not null)
                        {
                            return spdTry;
                        }
                    }

                    recentlyFailedPorts[port] = DateTimeOffset.UtcNow;
                }
            }

            int sec = (int)sw.Elapsed.TotalSeconds;
            if (sec >= lastReportedSec + 4)
            {
                lastReportedSec = sec;
                onStepProgress?.Invoke(1, 4, $"Step 1/4 in progress — Listening at 125 Hz ({sec}s / {(int)maxWait.TotalSeconds}s) — Plug powered-OFF phone now...");
            }

            await Task.Delay(FastVcomPollDelay, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static bool IsInCooldown(Dictionary<string, DateTimeOffset> map, string port) =>
        map.TryGetValue(port, out var ts) && (DateTimeOffset.UtcNow - ts) < TimeSpan.FromMilliseconds(450);

    private async Task<QuickPhoneReadReport?> CatchMediaTekEndpointAsync(
        DetectedUsbDevice dev,
        string port,
        string brandTag,
        TimeSpan elapsed,
        Action<int, int, string>? onStepProgress,
        Action<string>? onConsoleLine,
        CancellationToken ct)
    {
        var shortPort = port.StartsWith("WINUSB:", StringComparison.OrdinalIgnoreCase) ? "WinUSB Direct Bulk" : port;
        var labelWithPort = dev.FriendlyName.Contains(shortPort, StringComparison.OrdinalIgnoreCase)
            ? dev.FriendlyName
            : $"{dev.FriendlyName} ({shortPort})";
        onStepProgress?.Invoke(2, 4, $"Step 2/4 in progress — USB VCOM Detected on {shortPort} ({dev.VidPidDisplay})! Handshaking device...");
        onConsoleLine?.Invoke($"✓ USB VCOM Detected: {labelWithPort} [{dev.VidPidDisplay}]");
        onConsoleLine?.Invoke($"Handshaking device on {shortPort}...");

        var probe = await _mtk.ProbeReadOnlyBromOrPreloaderAsync(port, ct).ConfigureAwait(false);
        onStepProgress?.Invoke(3, 4, $"Step 3/4 in progress — Reading MediaTek SoC registers on {shortPort}...");

        if (!probe.SyncSucceeded)
        {
            onConsoleLine?.Invoke($"⚠ {probe.DiagnosticMessage} — Still listening at 125 Hz...");
            return null;
        }

        var stage = probe.LoaderStageLabel ?? dev.ClassifiedMode.ToString();
        var holdBadge = probe.PortHeldActive ? "ACTIVE (Phone held in VCOM — charging blocked)" : "Completed";
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Loader Stage", $"{stage} ({dev.VidPidDisplay})"),
            new("Active Endpoint", shortPort),
            new("VCOM Hold State", holdBadge),
            new("Chipset / SoC (DETECTED)", probe.ChipsetFamilySummary),
            new("Hardware Code (0xFD)", probe.HardwareCode.HasValue ? $"0x{probe.HardwareCode.Value:X4}" : "N/A"),
            new("HW Sub / HW Ver / SW Ver", $"0x{probe.HwSubCode ?? 0:X4} / 0x{probe.HwVersion ?? 0:X4} / 0x{probe.SwVersion ?? 0:X4}"),
            new("Secure Boot (SBC)", probe.SecureBootEnabled ? "Enabled (Signed images required)" : "Disabled"),
            new("Serial Link Auth (SLA)", probe.SerialLinkAuthRequired ? "Required" : "Not Required"),
            new("Download Agent Auth (DAA)", probe.DownloadAgentAuthRequired ? "Required (Signed OEM DA needed for flash)" : "Not Required"),
            new("SoC Unique ME_ID (0xE1)", string.IsNullOrWhiteSpace(probe.MeIdHex) ? "Not reported by stage" : probe.MeIdHex),
            new("Marketing Model", "Unidentified in BROM/Preloader (Never guessed from chipset alone)")
        };

        onConsoleLine?.Invoke($"✓ VCOM Handshake LOCKED on {shortPort}! Stage: {stage}");
        onConsoleLine?.Invoke($"  • Chipset  : {probe.ChipsetFamilySummary} [HW_CODE 0x{probe.HardwareCode ?? 0:X4}]");
        onConsoleLine?.Invoke($"  • Versions : Sub=0x{probe.HwSubCode ?? 0:X4} | HW=0x{probe.HwVersion ?? 0:X4} | SW=0x{probe.SwVersion ?? 0:X4}");
        onConsoleLine?.Invoke($"  • Security : SBC={probe.SecureBootEnabled} | SLA={probe.SerialLinkAuthRequired} | DAA={probe.DownloadAgentAuthRequired}");
        if (!string.IsNullOrWhiteSpace(probe.MeIdHex))
        {
            onConsoleLine?.Invoke($"  • ME_ID    : {probe.MeIdHex}");
        }
        if (probe.PortHeldActive)
        {
            onConsoleLine?.Invoke($"  • Hold     : {shortPort} HELD ACTIVE (phone parked in VCOM, prevented from battery charging)");
        }
        onStepProgress?.Invoke(4, 4, $"Step 4/4 complete — Read {probe.ChipsetFamilySummary} on {shortPort}.");

        _ = _discovery.RefreshInventoryAsync(CancellationToken.None);
        return new QuickPhoneReadReport(
            Succeeded: true,
            DetectedProtocolLabel: $"MediaTek {stage}",
            PortOrSerial: shortPort,
            BrandSummary: $"{brandTag} ({stage})",
            ModelSummary: "Unidentified in BROM/Preloader (Provenance: UNKNOWN)",
            ModelProvenance: DataProvenance.Unknown,
            ChipsetSummary: probe.ChipsetFamilySummary,
            AndroidOrBootVersion: $"{stage} • HW 0x{probe.HwVersion ?? 0:X4} • SW 0x{probe.SwVersion ?? 0:X4}",
            SecurityAndLockSummary: $"SBC={probe.SecureBootEnabled}, SLA={probe.SerialLinkAuthRequired}, DAA={probe.DownloadAgentAuthRequired}",
            SlotAndStorageSummary: probe.PortHeldActive ? "VCOM Held Active (Ready — charging blocked)" : "Requires OEM-signed DA for flash",
            GuidanceMessage: probe.DiagnosticMessage,
            DetailedFields: fields);
    }

    private async Task<QuickPhoneReadReport?> CatchUnisocEndpointAsync(
        DetectedUsbDevice dev,
        string port,
        string brandTag,
        TimeSpan elapsed,
        Action<int, int, string>? onStepProgress,
        Action<string>? onConsoleLine,
        CancellationToken ct)
    {
        var shortPort = port.StartsWith("WINUSB:", StringComparison.OrdinalIgnoreCase) ? "WinUSB Direct Bulk" : port;
        var labelWithPort = dev.FriendlyName.Contains(shortPort, StringComparison.OrdinalIgnoreCase)
            ? dev.FriendlyName
            : $"{dev.FriendlyName} ({shortPort})";
        onStepProgress?.Invoke(2, 4, $"Step 2/4 in progress — USB VCOM Detected on {shortPort} ({dev.VidPidDisplay})! Handshaking device...");
        onConsoleLine?.Invoke($"✓ USB VCOM Detected: {labelWithPort} [{dev.VidPidDisplay}]");
        onConsoleLine?.Invoke($"Handshaking device on {shortPort}...");

        var probe = await _unisoc.ProbeReadOnlyDownloadVersionAsync(port, ct).ConfigureAwait(false);
        onStepProgress?.Invoke(3, 4, $"Step 3/4 in progress — Decoding BSL_REP_VER frame from {shortPort}...");

        if (!probe.CheckBaudAckReceived)
        {
            onConsoleLine?.Invoke($"⚠ {probe.DiagnosticMessage}");
            return null;
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Transport Mode", $"Unisoc Download ({dev.VidPidDisplay})"),
            new("Active Endpoint", shortPort),
            new("VCOM Hold State", probe.PortHeldActive ? "ACTIVE (Phone held in Download mode)" : "Completed"),
            new("Boot ROM Version (0x81)", probe.BootRomBannerVersion ?? "Reported"),
            new("Signed FDL1 / FDL2 Policy", probe.RequiresSignedFdl ? "Required (Use validated .pac firmware)" : "Standard"),
            new("Marketing Model", "Unidentified in Boot ROM (Never guessed from chipset alone)")
        };

        onConsoleLine?.Invoke($"✓ Unisoc Boot ROM LOCKED on {shortPort}! Banner: '{probe.BootRomBannerVersion}'");
        onStepProgress?.Invoke(4, 4, $"Step 4/4 complete — Read Unisoc Boot ROM '{probe.BootRomBannerVersion}' on {shortPort}.");

        _ = _discovery.RefreshInventoryAsync(CancellationToken.None);
        return new QuickPhoneReadReport(
            Succeeded: true,
            DetectedProtocolLabel: "Unisoc / Spreadtrum Download VCOM",
            PortOrSerial: shortPort,
            BrandSummary: $"{brandTag} (Unisoc Boot ROM)",
            ModelSummary: "Unidentified in Boot ROM (Provenance: UNKNOWN)",
            ModelProvenance: DataProvenance.Unknown,
            ChipsetSummary: $"Unisoc / Spreadtrum ({probe.BootRomBannerVersion})",
            AndroidOrBootVersion: probe.BootRomBannerVersion ?? "Boot ROM",
            SecurityAndLockSummary: "Signed FDL1/FDL2 required for RAM/Flash operations",
            SlotAndStorageSummary: probe.PortHeldActive ? "VCOM Held Active (Charging blocked)" : "Inspect .pac in Firmware Tools",
            GuidanceMessage: probe.DiagnosticMessage,
            DetailedFields: fields);
    }

    private async Task<QuickPhoneReadReport?> RunParallelAdbAndFastbootLoopAsync(
        TimeSpan maxWait,
        Action<int, int, string>? onStepProgress,
        Action<string>? onConsoleLine,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        bool notifiedUnauthorizedAdb = false;
        bool notifiedChargeOnlyMtp = false;
        int cycle = 0;

        while (!ct.IsCancellationRequested && sw.Elapsed < maxWait)
        {
            var adbReport = await TryReadAdbDeviceAsync(onStepProgress, onConsoleLine, notifiedUnauthorizedAdb, ct).ConfigureAwait(false);
            if (adbReport.UnauthorizedSeen)
            {
                notifiedUnauthorizedAdb = true;
            }
            if (adbReport.Report is not null)
            {
                await _discovery.RefreshInventoryAsync(ct).ConfigureAwait(false);
                return adbReport.Report;
            }

            var fbReport = await TryReadFastbootDeviceAsync(onStepProgress, onConsoleLine, ct).ConfigureAwait(false);
            if (fbReport is not null)
            {
                await _discovery.RefreshInventoryAsync(ct).ConfigureAwait(false);
                return fbReport;
            }

            if (!notifiedChargeOnlyMtp && cycle >= 1)
            {
                var allUsb = await _usb.EnumerateUsbDevicesAsync(ct).ConfigureAwait(false);
                var mtpOnly = allUsb.FirstOrDefault(u => u.ClassifiedMode is ConnectionMode.MtpOnly or ConnectionMode.CompositeUsb);
                if (mtpOnly is not null)
                {
                    notifiedChargeOnlyMtp = true;
                    onConsoleLine?.Invoke($"ℹ Detected {mtpOnly.FriendlyName} [{mtpOnly.VidPidDisplay}] in Charge/MTP mode (powered ON without ADB).");
                    onConsoleLine?.Invoke("  👉 For VCOM Read: Unplug USB, power OFF phone completely, and plug USB back in (125 Hz VCOM catcher is still running!).");
                    onConsoleLine?.Invoke("  👉 For Powered-ON Read: Enable Developer Options -> USB Debugging on the phone.");
                }
            }

            cycle++;
            await Task.Delay(900, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<(QuickPhoneReadReport? Report, bool UnauthorizedSeen)> TryReadAdbDeviceAsync(
        Action<int, int, string>? onStepProgress,
        Action<string>? onConsoleLine,
        bool alreadyNotifiedUnauthorized,
        CancellationToken ct)
    {
        if (!await _adb.IsToolAvailableAsync(ct).ConfigureAwait(false))
        {
            return (null, alreadyNotifiedUnauthorized);
        }

        var endpoints = await _adb.ListDevicesAsync(ct).ConfigureAwait(false);
        if (endpoints.Count == 0)
        {
            return (null, alreadyNotifiedUnauthorized);
        }

        if (endpoints.Count > 1)
        {
            var selectedKey = _discovery.SelectedDevice?.TransportSerial;
            if (string.IsNullOrWhiteSpace(selectedKey))
            {
                throw new AmbiguousDeviceSelectionException(endpoints.Count);
            }
            endpoints = endpoints.Where(e => string.Equals(e.Serial, selectedKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (endpoints.Count == 0)
            {
                throw new AmbiguousDeviceSelectionException(2);
            }
        }

        var ep = endpoints[0];
        if (ep.State is AdbDeviceState.Unauthorized or AdbDeviceState.Authorizing)
        {
            if (!alreadyNotifiedUnauthorized)
            {
                onConsoleLine?.Invoke($"⚠ ADB device '{ep.Serial}' detected as UNAUTHORIZED! Unlock phone screen and tap 'Allow USB Debugging' now...");
                onStepProgress?.Invoke(2, 4, $"Step 2/4 in progress — Waiting for RSA 'Allow USB Debugging' tap on phone ({ep.Serial})...");
            }
            return (null, true);
        }

        if (ep.State is not (AdbDeviceState.Connected or AdbDeviceState.Recovery or AdbDeviceState.Sideload))
        {
            return (null, alreadyNotifiedUnauthorized);
        }

        onStepProgress?.Invoke(2, 4, $"Step 2/4 in progress — Connected to ADB ({ep.Serial}, {ep.State})! Reading device properties...");
        onConsoleLine?.Invoke($"⚡ Connected via ADB ({ep.State}) — Serial: {ep.Serial}");

        if (ep.State == AdbDeviceState.Sideload)
        {
            onConsoleLine?.Invoke("✓ Phone is in Recovery Sideload mode (ready for verified OTA ZIP sideload).");
            onStepProgress?.Invoke(4, 4, $"Step 4/4 complete — Recovery Sideload ready ({ep.Serial}).");
            return (new QuickPhoneReadReport(
                Succeeded: true,
                DetectedProtocolLabel: "Android Recovery Sideload",
                PortOrSerial: ep.Serial,
                BrandSummary: "Android Recovery",
                ModelSummary: ep.ModelHint ?? "Sideload Endpoint (DETECTED)",
                ModelProvenance: DataProvenance.Detected,
                ChipsetSummary: "Unavailable in Sideload-only transport",
                AndroidOrBootVersion: "Recovery Sideload",
                SecurityAndLockSummary: "Verified OTA signature enforced by Recovery",
                SlotAndStorageSummary: "Use ADB & Fastboot -> Recovery Sideload",
                GuidanceMessage: $"Device {ep.Serial} is ready in Recovery Sideload mode.",
                DetailedFields:
                [
                    new("Transport Mode", "ADB Sideload"),
                    new("Serial", ep.Serial)
                ]), alreadyNotifiedUnauthorized);
        }

        var props = await _adb.GetDevicePropertiesAsync(ep.Serial, ct).ConfigureAwait(false);
        onStepProgress?.Invoke(3, 4, "Step 3/4 in progress — Matching Transsion profile & reading battery/slot status...");
        var battery = await _adb.CaptureBatterySummaryAsync(ep.Serial, ct).ConfigureAwait(false);

        string GetProp(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (props.TryGetValue(k, out var val) && !string.IsNullOrWhiteSpace(val))
                {
                    return val.Trim();
                }
            }
            return string.Empty;
        }

        var brand = GetProp("ro.product.brand", "ro.product.manufacturer");
        var modelCode = GetProp("ro.product.model", "ro.product.system.model");
        var marketName = GetProp("ro.tran.market.name", "ro.product.marketname", "ro.vendor.tran.market.name");
        var codename = GetProp("ro.product.device", "ro.build.product");
        var platform = GetProp("ro.board.platform", "ro.hardware", "ro.mediatek.platform");
        var androidVer = GetProp("ro.build.version.release");
        var secPatch = GetProp("ro.build.version.security_patch");
        var displayBuild = GetProp("ro.build.display.id");
        var fingerprint = GetProp("ro.build.fingerprint");
        var slot = GetProp("ro.boot.slot_suffix");
        var vbState = GetProp("ro.boot.verifiedbootstate");
        var flashLocked = GetProp("ro.boot.flash.locked");

        var matchedProfile = _profiles.MatchVerifiedProfile(brand, modelCode, codename, fingerprint);
        var provenance = matchedProfile is not null ? DataProvenance.Verified : DataProvenance.Detected;
        var primaryCode = matchedProfile is { ModelCodes.Count: > 0 } ? matchedProfile.ModelCodes[0] : modelCode;
        var resolvedModelLabel = matchedProfile is not null
            ? $"{matchedProfile.MarketingName} ({primaryCode})"
            : !string.IsNullOrWhiteSpace(marketName)
                ? $"{marketName} ({modelCode})"
                : !string.IsNullOrWhiteSpace(modelCode) ? modelCode : ep.ModelHint ?? "Android Device";

        var lockText = flashLocked switch
        {
            "1" => $"Locked (AVB: {(string.IsNullOrEmpty(vbState) ? "green" : vbState)})",
            "0" => $"Unlocked (AVB: {(string.IsNullOrEmpty(vbState) ? "orange" : vbState)})",
            _ => string.IsNullOrEmpty(vbState) ? "Reported via OS" : $"AVB={vbState}"
        };

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Transport Mode", $"ADB ({ep.State})"),
            new("Serial Number", ep.Serial),
            new("Brand / Manufacturer", string.IsNullOrEmpty(brand) ? "Unknown" : brand),
            new($"Model ({provenance.ToString().ToUpperInvariant()})", resolvedModelLabel),
            new("Device Codename", string.IsNullOrEmpty(codename) ? "N/A" : codename),
            new("SoC / Board Platform", string.IsNullOrEmpty(platform) ? "N/A" : platform),
            new("Android Version", string.IsNullOrEmpty(androidVer) ? "N/A" : $"Android {androidVer} (Patch: {secPatch})"),
            new("Build Number", string.IsNullOrEmpty(displayBuild) ? "N/A" : displayBuild),
            new("Bootloader & AVB", lockText),
            new("Active Slot", string.IsNullOrEmpty(slot) ? "Single-Slot / Non-A/B" : slot),
            new("Battery Summary", string.IsNullOrWhiteSpace(battery) ? "N/A" : battery.Replace('\n', ' ').Trim())
        };

        onConsoleLine?.Invoke($"✓ Read Complete: {brand} {resolvedModelLabel} [{provenance.ToString().ToUpperInvariant()}]");
        onConsoleLine?.Invoke($"  • Platform : {platform} | Android {androidVer} ({secPatch})");
        onConsoleLine?.Invoke($"  • Security : {lockText} | Slot: {(string.IsNullOrEmpty(slot) ? "Non-A/B" : slot)}");
        onStepProgress?.Invoke(4, 4, $"Step 4/4 complete — Read {brand} {resolvedModelLabel} via ADB.");

        return (new QuickPhoneReadReport(
            Succeeded: true,
            DetectedProtocolLabel: $"ADB ({ep.State})",
            PortOrSerial: ep.Serial,
            BrandSummary: string.IsNullOrEmpty(brand) ? "Android" : brand.ToUpperInvariant(),
            ModelSummary: resolvedModelLabel,
            ModelProvenance: provenance,
            ChipsetSummary: string.IsNullOrEmpty(platform) ? "Reported via ADB" : platform,
            AndroidOrBootVersion: string.IsNullOrEmpty(androidVer) ? displayBuild : $"Android {androidVer} • Patch {secPatch}",
            SecurityAndLockSummary: lockText,
            SlotAndStorageSummary: string.IsNullOrEmpty(slot) ? "Standard Partitioning" : $"A/B Dynamic (Active: {slot})",
            GuidanceMessage: $"Successfully read {brand} {resolvedModelLabel} via {ep.State}.",
            DetailedFields: fields), alreadyNotifiedUnauthorized);
    }

    private async Task<QuickPhoneReadReport?> TryReadFastbootDeviceAsync(
        Action<int, int, string>? onStepProgress,
        Action<string>? onConsoleLine,
        CancellationToken ct)
    {
        if (!await _fastboot.IsToolAvailableAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var fbEndpoints = await _fastboot.ListDevicesAsync(ct).ConfigureAwait(false);
        if (fbEndpoints.Count == 0)
        {
            return null;
        }

        if (fbEndpoints.Count > 1)
        {
            var selectedKey = _discovery.SelectedDevice?.TransportSerial;
            if (string.IsNullOrWhiteSpace(selectedKey))
            {
                throw new AmbiguousDeviceSelectionException(fbEndpoints.Count);
            }
            fbEndpoints = fbEndpoints.Where(e => string.Equals(e.Serial, selectedKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (fbEndpoints.Count == 0)
            {
                throw new AmbiguousDeviceSelectionException(2);
            }
        }

        var ep = fbEndpoints[0];
        onStepProgress?.Invoke(2, 4, $"Step 2/4 in progress — Connected via {ep.Mode} ({ep.Serial})! Reading bootloader variables...");
        onConsoleLine?.Invoke($"⚡ Connected via {ep.Mode} — Serial: {ep.Serial}");

        var vars = await _fastboot.GetAllVariablesAsync(ep.Serial, ct).ConfigureAwait(false);
        onStepProgress?.Invoke(3, 4, "Step 3/4 in progress — Evaluating lock state, active slot & partition layout...");

        string GetVar(string key) => vars.TryGetValue(key, out var v) ? v : string.Empty;
        var product = GetVar("product");
        var unlocked = GetVar("unlocked");
        var slot = GetVar("current-slot");
        var isUserspace = GetVar("is-userspace");
        var maxDl = GetVar("max-download-size");
        var blVer = GetVar("version-bootloader");

        var matched = _profiles.MatchVerifiedProfile(null, null, product, null);
        var prov = matched is not null ? DataProvenance.Verified : DataProvenance.Detected;
        var matchedCode = matched is { ModelCodes.Count: > 0 } ? matched.ModelCodes[0] : product;
        var modelText = matched is not null
            ? $"{matched.MarketingName} ({matchedCode})"
            : !string.IsNullOrEmpty(product) ? $"Product: {product}" : "Fastboot Device";

        var lockSummary = string.Equals(unlocked, "yes", StringComparison.OrdinalIgnoreCase)
            ? "Bootloader UNLOCKED"
            : string.Equals(unlocked, "no", StringComparison.OrdinalIgnoreCase)
                ? "Bootloader LOCKED"
                : $"unlocked={unlocked}";

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Transport Mode", $"{ep.Mode} (is-userspace={isUserspace})"),
            new("Serial Number", ep.Serial),
            new($"Product / Model ({prov.ToString().ToUpperInvariant()})", modelText),
            new("Bootloader Lock State", lockSummary),
            new("Current Active Slot", string.IsNullOrEmpty(slot) ? "N/A" : slot),
            new("Bootloader Version", string.IsNullOrEmpty(blVer) ? "N/A" : blVer),
            new("Max Download Size", string.IsNullOrEmpty(maxDl) ? "N/A" : maxDl)
        };

        onConsoleLine?.Invoke($"✓ Read Complete via {ep.Mode}: {modelText} | {lockSummary} | Slot: {(string.IsNullOrEmpty(slot) ? "N/A" : slot)}");
        onStepProgress?.Invoke(4, 4, $"Step 4/4 complete — Read {modelText} via {ep.Mode}.");

        return new QuickPhoneReadReport(
            Succeeded: true,
            DetectedProtocolLabel: ep.Mode == ConnectionMode.FastbootdUserspace ? "Userspace Fastbootd" : "Bootloader Fastboot",
            PortOrSerial: ep.Serial,
            BrandSummary: matched?.Manufacturer ?? "FASTBOOT",
            ModelSummary: modelText,
            ModelProvenance: prov,
            ChipsetSummary: matched?.ChipsetFamily ?? product,
            AndroidOrBootVersion: string.IsNullOrEmpty(blVer) ? ep.Mode.ToString() : $"Bootloader {blVer}",
            SecurityAndLockSummary: lockSummary,
            SlotAndStorageSummary: string.IsNullOrEmpty(slot) ? $"MaxDL {maxDl}" : $"Active Slot: {slot} • MaxDL {maxDl}",
            GuidanceMessage: $"Read {modelText} in {ep.Mode}. Ready for Partition Manager or Safe Flasher.",
            DetailedFields: fields);
    }
}
