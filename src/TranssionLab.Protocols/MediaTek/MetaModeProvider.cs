using System.Text;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;

namespace TranssionLab.Protocols.MediaTek;

/// <summary>
/// Dedicated MediaTek META Mode &amp; Read-Only Modem AT provider (Section 2D).
/// Reports honest USB interface detection, checks whether an authorized external META library is installed,
/// and supports strictly read-only 3GPP AT identification queries (ATI / AT+CGMI / AT+CGMM / AT+CGMR).
/// Never fabricates META connectivity or modifies NVRAM/IMEI/calibration data.
/// </summary>
public sealed class MetaModeProvider : IMetaModeProvider
{
    private readonly IAppSettingsService _settings;
    private readonly IStructuredLogger _logger;
    private readonly ISerialChannelFactory? _serialFactory;

    public string ProviderName => "MediaTek META Mode";

    public ProviderImplementationStatus ImplementationStatus =>
        IsAuthorizedMetaLibraryInstalled(out _)
            ? ProviderImplementationStatus.ReadOnlyDiagnostics
            : ProviderImplementationStatus.DetectionOnly;

    public MetaModeProvider(
        IAppSettingsService settings,
        IStructuredLogger logger,
        ISerialChannelFactory? serialFactory = null)
    {
        _settings = settings;
        _logger = logger;
        _serialFactory = serialFactory;
    }

    public bool IsAuthorizedMetaLibraryInstalled(out string statusDetail)
    {
        var configuredPath = _settings.Current.OptionalMetaLibraryPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            statusDetail = "No external licensed MediaTek META communication library (MetaCore.dll / SPMETA_DLL.dll) is configured in Settings. Operating in honest USB Detection-Only mode.";
            return false;
        }

        if (!File.Exists(configuredPath))
        {
            statusDetail = $"Configured META library path does not exist: '{configuredPath}'. Operating in USB Detection-Only mode.";
            return false;
        }

        statusDetail = $"External META library file present at '{Path.GetFileName(configuredPath)}'. OEM SLA/META authentication restrictions still apply on secure devices.";
        return true;
    }

    public IReadOnlyList<MetaInterfaceDetection> DetectMetaInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory)
    {
        var hasLib = IsAuthorizedMetaLibraryInstalled(out _);
        var detections = new List<MetaInterfaceDetection>();

        foreach (var usb in usbInventory.Where(u => u.ClassifiedMode == ConnectionMode.MediaTekMeta))
        {
            var state = usb.DriverDiagnostics.Status != DriverHealthStatus.Healthy
                ? ConnectionDashboardState.ProtocolUnsupported
                : hasLib
                    ? ConnectionDashboardState.AuthenticationRequired
                    : ConnectionDashboardState.ProtocolUnsupported;

            detections.Add(new MetaInterfaceDetection(
                UsbDevice: usb,
                InterfaceClassification: $"MediaTek META USB Endpoint ({usb.VidPidDisplay}, Port={usb.AssociatedComPort ?? "Unmapped"})",
                ExternalCommunicationProviderAvailable: hasLib,
                ReportedState: state,
                DocumentedCapabilities:
                [
                    "USB VID/PID/MI META endpoint identification",
                    "Windows VCOM / ETS driver health diagnostics",
                    "Read-only AP/Modem build inquiry (requires licensed OEM META DLL + unlocked/authorized target)"
                ],
                Prerequisites:
                [
                    "Signed MediaTek USB VCOM / ETS driver bound without Code 28/52",
                    "Licensed OEM ModemMETA communication provider DLL configured locally",
                    "Device firmware that permits META entry (blocked by SLA/META-lock on many retail HiOS/XOS builds)"
                ]));
        }

        return detections;
    }

    public Task<MetaSessionDiagnosticResult> DiagnoseMetaConnectionAsync(DetectedUsbDevice usbDevice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usbDevice);

        if (usbDevice.ClassifiedMode != ConnectionMode.MediaTekMeta)
        {
            const string msg = "Selected USB endpoint is NOT a recognized MediaTek META interface. META connectivity will not be fabricated.";
            _logger.LogWarning(ProviderName, "DiagnoseMetaConnection", msg, usbDevice.InstanceId, "meta.diagnose_interface");
            return Task.FromResult(new MetaSessionDiagnosticResult(
                UsbEndpointVerified: false,
                CommunicationProviderAvailable: false,
                SessionEstablished: false,
                State: ConnectionDashboardState.ProtocolUnsupported,
                DiagnosticSummary: msg,
                Limitations: ["Generic MTP/ADB/VCOM devices cannot be treated as active META sessions."]));
        }

        var hasLib = IsAuthorizedMetaLibraryInstalled(out var libStatus);
        var driverHealthy = usbDevice.DriverDiagnostics.Status == DriverHealthStatus.Healthy;

        var summary = !driverHealthy
            ? $"META USB descriptor detected ({usbDevice.VidPidDisplay}), but Windows driver reports: {usbDevice.DriverDiagnostics.Summary}"
            : !hasLib
                ? $"META USB descriptor verified ({usbDevice.VidPidDisplay} on {usbDevice.AssociatedComPort ?? "USB"}), but active protocol communication is unavailable: {libStatus}"
                : $"META USB descriptor verified ({usbDevice.VidPidDisplay}). Active META session requires vendor META authentication certificate on modern Transsion builds.";

        var resultState = !driverHealthy || !hasLib
            ? ConnectionDashboardState.ProtocolUnsupported
            : ConnectionDashboardState.AuthenticationRequired;

        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = LogSeverity.Info,
            DeviceId = usbDevice.InstanceId,
            ProtocolProvider = ProviderName,
            UsbInterface = usbDevice.VidPidDisplay,
            OperationId = "meta.diagnose_interface",
            RequestedAction = "Verify META Mode Interface & Provider Readiness",
            VerificationResult = summary
        });

        return Task.FromResult(new MetaSessionDiagnosticResult(
            UsbEndpointVerified: true,
            CommunicationProviderAvailable: hasLib,
            SessionEstablished: false,
            State: resultState,
            DiagnosticSummary: summary,
            Limitations:
            [
                "TranssionLab does not bundle proprietary MediaTek ModemMETA binaries.",
                "Protected NVRAM / IMEI / RF calibration modifications are not implemented."
            ]));
    }

    public async Task<ReadOnlyModemAtInfoResult> QueryReadOnlyModemAtInfoAsync(string comPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comPort);
        var cleanPort = PlatformToolLocator.NormalizeSerialPortName(comPort);
        var factory = _serialFactory ?? new SystemSerialChannelFactory();

        try
        {
            using var ch = factory.Create(cleanPort, 115200, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(350));
            ch.Open();

            var probe = await SendAtLineAsync(ch, "AT\r\n", ct).ConfigureAwait(false);
            if (!probe.Contains("OK", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(probe))
            {
                var noAck = $"Opened {cleanPort}, sent standard 'AT', but received no AT modem response. Verify the selected COM port is a Modem / AT / Diag port (not raw BROM).";
                _logger.LogWarning(ProviderName, $"Read-Only AT Probe ({cleanPort})", noAck, cleanPort, "meta.readonly_at_info");
                return new ReadOnlyModemAtInfoResult(true, false, null, null, null, noAck);
            }

            var ati = CleanAtResponse(await SendAtLineAsync(ch, "ATI\r\n", ct).ConfigureAwait(false), "ATI");
            var cgmi = CleanAtResponse(await SendAtLineAsync(ch, "AT+CGMI\r\n", ct).ConfigureAwait(false), "AT+CGMI");
            var cgmm = CleanAtResponse(await SendAtLineAsync(ch, "AT+CGMM\r\n", ct).ConfigureAwait(false), "AT+CGMM");
            var cgmr = CleanAtResponse(await SendAtLineAsync(ch, "AT+CGMR\r\n", ct).ConfigureAwait(false), "AT+CGMR");

            var summary = $"AT Handshake OK on {cleanPort} | Vendor: {cgmi ?? "N/A"} | Model: {cgmm ?? "N/A"} | Baseband/FW: {cgmr ?? ati ?? "N/A"}";
            _logger.LogInfo(ProviderName, $"Read-Only AT Probe ({cleanPort})", summary, cleanPort, "meta.readonly_at_info");
            return new ReadOnlyModemAtInfoResult(
                PortOpened: true,
                AtAckReceived: true,
                Manufacturer: cgmi,
                Model: cgmm,
                FirmwareRevision: cgmr ?? ati,
                RawSummary: summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var err = $"Could not open or query {cleanPort}: {ex.Message}";
            _logger.LogError(ProviderName, $"Read-Only AT Probe ({cleanPort})", err, null, cleanPort, "meta.readonly_at_info");
            return new ReadOnlyModemAtInfoResult(false, false, null, null, null, err);
        }
    }

    private static async Task<string> SendAtLineAsync(ISerialByteChannel ch, string cmd, CancellationToken ct)
    {
        await ch.WriteAsync(Encoding.ASCII.GetBytes(cmd), ct).ConfigureAwait(false);
        var sb = new StringBuilder(256);
        var buf = new byte[128];
        for (int i = 0; i < 4; i++)
        {
            int n = await ch.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().Contains("\nOK", StringComparison.OrdinalIgnoreCase) ||
                sb.ToString().StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
                sb.ToString().Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
        return sb.ToString();
    }

    private static string? CleanAtResponse(string raw, string echoedCmd)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !string.Equals(l, "OK", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(l, "ERROR", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(l, echoedCmd, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Contains(':', StringComparison.Ordinal) ? l[(l.IndexOf(':') + 1)..].Trim() : l)
            .ToList();

        return lines.Count > 0 ? string.Join(" / ", lines) : null;
    }
}
