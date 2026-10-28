using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Usb;

namespace TranssionLab.Protocols.MediaTek;

/// <summary>
/// MediaTek Preloader / Boot ROM (BROM) / DA capability-aware provider (Section 2B).
/// Strictly separates USB interface detection from active protocol communication,
/// implements state-verified 0xA0 0x0A 0x50 0x05 catch-and-hold (handling both Preloader "READY" banners
/// and Preloader i=0 state-machine resets when multiple 0xA0 bytes hit USB FIFO),
/// reads documented read-only identification &amp; security registers (0xFD, 0xFC, 0xD8, 0xFE, 0xE1),
/// and holds the active VCOM session with read-only watchdog pings on Windows so the phone does not
/// fall back into battery charging while the technician inspects the device.
/// </summary>
public sealed class MediaTekProvider : IMediaTekProvider, IDisposable
{
    private readonly ISerialChannelFactory _serialFactory;
    private readonly IStructuredLogger _logger;
    private readonly object _holdGate = new();
    private ISerialByteChannel? _heldChannel;
    private CancellationTokenSource? _holdCts;
    private string? _heldPortName;

    public string ProviderName => "MediaTek BROM / Preloader / DA";
    public ProviderImplementationStatus ImplementationStatus => ProviderImplementationStatus.ReadOnlyDiagnostics;

    public string? HeldPortName
    {
        get
        {
            lock (_holdGate)
            {
                return _heldChannel is { IsOpen: true } ? _heldPortName : null;
            }
        }
    }

    public MediaTekProvider(ISerialChannelFactory serialFactory, IStructuredLogger logger)
    {
        _serialFactory = serialFactory;
        _logger = logger;
    }

    public IReadOnlyList<DetectedUsbDevice> FilterMediaTekInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory) =>
        usbInventory
            .Where(u => u.VendorId == UsbSignatureCatalog.VidMediaTek ||
                        u.ClassifiedMode is ConnectionMode.MediaTekBrom
                            or ConnectionMode.MediaTekPreloader
                            or ConnectionMode.MediaTekDownloadAgent)
            .ToList();

    public void ReleaseHeldPort()
    {
        ISerialByteChannel? oldChannel;
        CancellationTokenSource? oldCts;
        lock (_holdGate)
        {
            oldChannel = _heldChannel;
            oldCts = _holdCts;
            _heldChannel = null;
            _holdCts = null;
            _heldPortName = null;
        }

        try { oldCts?.Cancel(); } catch { }
        try { oldCts?.Dispose(); } catch { }
        try { oldChannel?.Dispose(); } catch { }
    }

    public async Task<PreloaderBootHandshakeResult> RequestPreloaderBootModeAsync(
        string comPort,
        PreloaderBootRequestKind mode = PreloaderBootRequestKind.MetaModeUsb,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comPort);
        var cleanPort = PlatformToolLocator.NormalizeSerialPortName(comPort);
        ReleaseHeldPort();

        var (sendTokenStr, expectedAckStr) = mode switch
        {
            PreloaderBootRequestKind.MetaModeStandard => ("METAMETA", "ATEMATEM"),
            PreloaderBootRequestKind.FastbootMode => ("FASTBOOT", "TOOBTSAF"),
            PreloaderBootRequestKind.FactoryMode => ("FACTFACT", "TCAFTCAF"),
            _ => ("ADVEMETA", "ATEMEVDA")
        };

        byte[] txToken = System.Text.Encoding.ASCII.GetBytes(sendTokenStr);
        byte[] txFallbackMeta = "METAMETA"u8.ToArray();

        try
        {
            using var channel = _serialFactory.Create(cleanPort, 115200, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(250));
            channel.Open();

            var rollingAscii = new System.Text.StringBuilder(128);
            var rxChunk = new byte[32];
            int maxCycles = OperatingSystem.IsWindows() ? 45 : 8;

            // Drain initial "READY" banner if already waiting in Preloader's TX FIFO
            int initRead = await channel.ReadAsync(rxChunk, ct).ConfigureAwait(false);
            if (initRead > 0)
            {
                AppendPrintableAscii(rollingAscii, rxChunk, initRead);
            }

            for (int cycle = 0; cycle < maxCycles; cycle++)
            {
                ct.ThrowIfCancellationRequested();

                // On MetaModeUsb, send ADVEMETA primarily and fall back to METAMETA on later cycles for older chipsets
                byte[] tokenToSend = (mode == PreloaderBootRequestKind.MetaModeUsb && cycle >= 28 && cycle % 2 == 1)
                    ? txFallbackMeta
                    : txToken;

                await channel.WriteAsync(tokenToSend, ct).ConfigureAwait(false);

                for (int r = 0; r < 3; r++)
                {
                    int n = await channel.ReadAsync(rxChunk, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    AppendPrintableAscii(rollingAscii, rxChunk, n);
                    var window = rollingAscii.ToString();
                    string? matchedAck = null;
                    if (window.Contains(expectedAckStr, StringComparison.Ordinal))
                    {
                        matchedAck = expectedAckStr;
                    }
                    else if (mode == PreloaderBootRequestKind.MetaModeUsb && window.Contains("ATEMATEM", StringComparison.Ordinal))
                    {
                        matchedAck = "ATEMATEM";
                    }

                    if (matchedAck is not null)
                    {
                        var okMsg = $"Preloader on {cleanPort} acknowledged {sendTokenStr} -> {matchedAck}! Phone is booting into {mode} (battery charging stopped).";
                        _logger.LogInfo(ProviderName, $"Preloader Boot Handshake ({cleanPort})", okMsg, cleanPort, "mtk.preloader_boot_mode");
                        return new PreloaderBootHandshakeResult(true, true, sendTokenStr, matchedAck, okMsg);
                    }
                }

                if (!OperatingSystem.IsWindows() && rollingAscii.Length == 0 && cycle >= 2)
                {
                    break;
                }
            }

            var failMsg = $"Opened {cleanPort}, sent {sendTokenStr}, but Preloader did not return {expectedAckStr} (received: '{rollingAscii}'). Ensure phone is powered OFF and connects in Preloader VCOM (0E8D:2000) without holding BROM-force keys.";
            _logger.LogWarning(ProviderName, $"Preloader Boot Handshake ({cleanPort})", failMsg, cleanPort, "mtk.preloader_boot_mode");
            return new PreloaderBootHandshakeResult(true, false, sendTokenStr, null, failMsg);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ProviderName, $"Preloader Boot Handshake ({cleanPort})", ex.Message, null, cleanPort, "mtk.preloader_boot_mode");
            return new PreloaderBootHandshakeResult(false, false, sendTokenStr, null, $"Could not handshake {cleanPort}: {ex.Message}");
        }
    }

    private static void AppendPrintableAscii(System.Text.StringBuilder sb, byte[] buf, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = buf[i];
            if (b is >= 0x20 and <= 0x7E)
            {
                sb.Append((char)b);
            }
        }
        if (sb.Length > 96)
        {
            sb.Remove(0, sb.Length - 64);
        }
    }

    public async Task<MediaTekHandshakeProbeResult> ProbeReadOnlyBromOrPreloaderAsync(string comPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comPort);
        var cleanPort = PlatformToolLocator.NormalizeSerialPortName(comPort);

        // Always release any previously held VCOM handle first so we never contend with ourselves
        ReleaseHeldPort();

        ISerialByteChannel? channel = null;
        bool transferredToHold = false;

        try
        {
            channel = _serialFactory.Create(cleanPort, 115200, TimeSpan.FromMilliseconds(22), TimeSpan.FromMilliseconds(250));
            channel.Open();

            var (syncOk, lastSeenByte) = await PerformCatchAndHoldSyncAsync(channel, ct).ConfigureAwait(false);
            if (!syncOk)
            {
                var failMsg = $"Handshake sync did not lock on {cleanPort} (last byte 0x{lastSeenByte:X2}). Ensure phone is powered OFF completely before plugging USB cable.";
                _logger.LogWarning(ProviderName, $"BROM/Preloader Sync ({cleanPort})", failMsg, cleanPort, "mtk.brom_readonly_probe");
                return new MediaTekHandshakeProbeResult(
                    PortOpened: true,
                    SyncSucceeded: false,
                    HardwareCode: null,
                    HwSubCode: null,
                    HwVersion: null,
                    SwVersion: null,
                    SecureBootEnabled: false,
                    SerialLinkAuthRequired: false,
                    DownloadAgentAuthRequired: false,
                    ChipsetFamilySummary: "Unidentified (Sync incomplete)",
                    ResultingState: ConnectionDashboardState.DetectedButUnidentified,
                    DiagnosticMessage: failMsg);
            }

            // 2. Documented read-only GET_HW_CODE (0xFD) — ALSO proves Preloader exited start_cmd loop!
            // If an extra buffered 0xA0 reset Preloader's start_cmd index i=0, Preloader echoes ~0xFD = 0x02 instead of 0xFD.
            // In that exact case we execute one clean 1-by-1 0xA0 0x0A 0x50 0x05 resync and re-issue 0xFD!
            ushort? hwCode = null;
            await channel.WriteAsync(new byte[] { 0xFD }, ct).ConfigureAwait(false);
            var hwBuf = await ReadEchoedCommandFrameAsync(channel, expectedEcho: 0xFD, trailingBytes: 4, ct).ConfigureAwait(false);
            if (hwBuf.Length == 1 && hwBuf[0] == 0x02)
            {
                bool reSynced = await PerformDeterministicFourByteSyncAsync(channel, ct).ConfigureAwait(false);
                if (reSynced)
                {
                    await channel.WriteAsync(new byte[] { 0xFD }, ct).ConfigureAwait(false);
                    hwBuf = await ReadEchoedCommandFrameAsync(channel, expectedEcho: 0xFD, trailingBytes: 4, ct).ConfigureAwait(false);
                }
            }

            if (hwBuf.Length == 5 && hwBuf[0] == 0xFD)
            {
                hwCode = (ushort)((hwBuf[1] << 8) | hwBuf[2]);
            }

            // 3. Documented read-only GET_HW_SW_VER (0xFC): echoes 0xFC, returns hw_sub(2), hw_ver(2), sw_ver(2), status(2)
            ushort? hwSub = null, hwVer = null, swVer = null;
            await channel.WriteAsync(new byte[] { 0xFC }, ct).ConfigureAwait(false);
            var verBuf = await ReadEchoedCommandFrameAsync(channel, expectedEcho: 0xFC, trailingBytes: 8, ct).ConfigureAwait(false);
            if (verBuf.Length == 9 && verBuf[0] == 0xFC)
            {
                hwSub = (ushort)((verBuf[1] << 8) | verBuf[2]);
                hwVer = (ushort)((verBuf[3] << 8) | verBuf[4]);
                swVer = (ushort)((verBuf[5] << 8) | verBuf[6]);
            }

            // 4. Documented read-only GET_TARGET_CONFIG (0xD8): echoes 0xD8, returns u32 configFlags + u16 status
            bool sbc = false, sla = false, daa = false;
            await channel.WriteAsync(new byte[] { 0xD8 }, ct).ConfigureAwait(false);
            var cfgBuf = await ReadEchoedCommandFrameAsync(channel, expectedEcho: 0xD8, trailingBytes: 6, ct).ConfigureAwait(false);
            if (cfgBuf.Length == 7 && cfgBuf[0] == 0xD8)
            {
                uint flags = ((uint)cfgBuf[1] << 24) | ((uint)cfgBuf[2] << 16) | ((uint)cfgBuf[3] << 8) | cfgBuf[4];
                sbc = (flags & 0x01) != 0; // Secure Boot Enable
                sla = (flags & 0x02) != 0; // Serial Link Authentication
                daa = (flags & 0x04) != 0; // Download Agent Authentication
            }

            // 5. Optional read-only GET_BL_VER (0xFE): 0xFE echo = BootROM, >=0x01 = Preloader protocol version
            string loaderStage = "BROM / Preloader";
            await channel.WriteAsync(new byte[] { 0xFE }, ct).ConfigureAwait(false);
            var blByte = new byte[1];
            int blRead = await channel.ReadAsync(blByte, ct).ConfigureAwait(false);
            if (blRead == 1)
            {
                loaderStage = blByte[0] == 0xFE
                    ? "BootROM (BROM)"
                    : $"Preloader (v{blByte[0]})";
            }

            // 6. Optional read-only GET_ME_ID (0xE1): echoes 0xE1 + u32 length(16) + 16-byte SoC ME_ID + u16 status
            string? meIdHex = await TryReadMeIdAsync(channel, ct).ConfigureAwait(false);

            // 7. On live Windows hardware ports, hold the synced VCOM session active with read-only 0xFD watchdog pings
            // so Preloader stays parked in VCOM mode instead of resetting into the battery-charging screen!
            if (OperatingSystem.IsWindows() && _serialFactory is SystemSerialChannelFactory)
            {
                HoldWindowsVcomSessionActive(cleanPort, channel);
                transferredToHold = true;
            }

            var chipsetSummary = hwCode.HasValue
                ? ResolveChipsetFamilyFromHwCode(hwCode.Value)
                : "Unknown MediaTek SoC";

            var authRestricted = sla || daa;
            var state = authRestricted
                ? ConnectionDashboardState.AuthenticationRequired
                : ConnectionDashboardState.ProtocolAvailable;

            var holdNote = transferredToHold ? " [VCOM HELD ACTIVE — charging blocked]" : string.Empty;
            var diag = authRestricted
                ? $"Caught & held {loaderStage} on {cleanPort} ({chipsetSummary}, HW=0x{hwCode ?? 0:X4}){holdNote}. Hardware enforces SBC={sbc}, SLA={sla}, DAA={daa}. Manufacturer-signed Download Agent and auth certificate are required for storage/flashing; security bypasses are not implemented."
                : $"Caught & held {loaderStage} on {cleanPort} ({chipsetSummary}, HW=0x{hwCode ?? 0:X4}){holdNote}. SBC={sbc}, SLA={sla}, DAA={daa}. Note: exact phone model is never inferred from SoC code alone.";

            _logger.Log(new StructuredLogEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                SessionId = _logger.CurrentSessionId,
                SequenceNumber = 0,
                Severity = LogSeverity.Info,
                DeviceId = cleanPort,
                ProtocolProvider = ProviderName,
                UsbInterface = cleanPort,
                OperationId = "mtk.brom_readonly_probe",
                RequestedAction = $"Catch-and-hold {loaderStage} handshake on {cleanPort}",
                CommandOutput = $"STAGE={loaderStage}, HW_CODE=0x{hwCode ?? 0:X4}, SBC={sbc}, SLA={sla}, DAA={daa}, ME_ID={meIdHex ?? "N/A"}",
                VerificationResult = diag
            });

            return new MediaTekHandshakeProbeResult(
                PortOpened: true,
                SyncSucceeded: true,
                HardwareCode: hwCode,
                HwSubCode: hwSub,
                HwVersion: hwVer,
                SwVersion: swVer,
                SecureBootEnabled: sbc,
                SerialLinkAuthRequired: sla,
                DownloadAgentAuthRequired: daa,
                ChipsetFamilySummary: chipsetSummary,
                ResultingState: state,
                DiagnosticMessage: diag,
                LoaderStageLabel: loaderStage,
                MeIdHex: meIdHex,
                PortHeldActive: transferredToHold);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ProviderName, $"BROM Probe ({cleanPort})", ex.Message, null, cleanPort, "mtk.brom_readonly_probe");
            return new MediaTekHandshakeProbeResult(
                false, false, null, null, null, null, false, false, false,
                "Probe failed",
                ConnectionDashboardState.OperationFailed,
                $"Unable to open or query {cleanPort}: {ex.Message}");
        }
        finally
        {
            if (!transferredToHold)
            {
                try { channel?.Dispose(); } catch { }
            }
        }
    }

    private void HoldWindowsVcomSessionActive(string portName, ISerialByteChannel channel)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        lock (_holdGate)
        {
            _heldChannel = channel;
            _heldCtsCleanup();
            _holdCts = cts;
            _heldPortName = portName;
        }

        _ = Task.Run(async () =>
        {
            var pingCmd = new byte[] { 0xFD };
            var rxDiscard = new byte[8];
            try
            {
                while (!cts.Token.IsCancellationRequested && channel.IsOpen)
                {
                    await Task.Delay(850, cts.Token).ConfigureAwait(false);
                    lock (_holdGate)
                    {
                        if (!ReferenceEquals(_heldChannel, channel))
                        {
                            return;
                        }
                    }

                    // Harmless read-only GET_HW_CODE (0xFD) kicks Preloader's usbdl watchdog so it stays in VCOM mode
                    await channel.WriteAsync(pingCmd, cts.Token).ConfigureAwait(false);
                    _ = await channel.ReadAsync(rxDiscard, cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Device unplugged or session released
            }
            finally
            {
                bool shouldRelease = false;
                lock (_holdGate)
                {
                    if (ReferenceEquals(_heldChannel, channel))
                    {
                        shouldRelease = true;
                    }
                }
                if (shouldRelease)
                {
                    ReleaseHeldPort();
                }
            }
        });
    }

    private void _heldCtsCleanup()
    {
        if (_holdCts is not null)
        {
            try { _holdCts.Cancel(); } catch { }
            try { _holdCts.Dispose(); } catch { }
            _holdCts = null;
        }
    }

    private static async Task<string?> TryReadMeIdAsync(ISerialByteChannel channel, CancellationToken ct)
    {
        await channel.WriteAsync(new byte[] { 0xE1 }, ct).ConfigureAwait(false);
        var hdr = await ReadEchoedCommandFrameAsync(channel, expectedEcho: 0xE1, trailingBytes: 4, ct).ConfigureAwait(false);
        if (hdr.Length != 5 || hdr[0] != 0xE1)
        {
            return null;
        }

        uint len = ((uint)hdr[1] << 24) | ((uint)hdr[2] << 16) | ((uint)hdr[3] << 8) | hdr[4];
        if (len is < 8 or > 32)
        {
            return null;
        }

        int tailLen = (int)len + 2;
        var body = new byte[tailLen];
        int offset = 0;
        int emptyReads = 0;
        while (offset < tailLen && emptyReads < 8)
        {
            int n = await channel.ReadAsync(body.AsMemory(offset, tailLen - offset), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                emptyReads++;
                continue;
            }
            emptyReads = 0;
            offset += n;
        }

        return offset >= (int)len ? Convert.ToHexString(body.AsSpan(0, (int)len)) : null;
    }

    private static async Task<(bool Synced, byte LastRx)> PerformCatchAndHoldSyncAsync(ISerialByteChannel channel, CancellationToken ct)
    {
        var txA0 = new byte[] { 0xA0 };
        var rx = new byte[1];
        byte lastSeen = 0x00;
        int maxAttempts = OperatingSystem.IsWindows() ? 110 : 12;
        int consecutiveZeroReads = 0;

        bool got5F = false;
        for (int attempt = 0; attempt < maxAttempts && !got5F; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await channel.WriteAsync(txA0, ct).ConfigureAwait(false);

            // Drain up to 10 startup banner bytes ("READY\r\n") if Preloader emitted them before 0x5F
            for (int drain = 0; drain < 10; drain++)
            {
                int n = await channel.ReadAsync(rx, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    consecutiveZeroReads++;
                    break;
                }

                consecutiveZeroReads = 0;
                lastSeen = rx[0];
                if (rx[0] == 0x5F)
                {
                    got5F = true;
                    break;
                }

                if (!OperatingSystem.IsWindows() && rx[0] is not ((byte)'R' or (byte)'E' or (byte)'A' or (byte)'D' or (byte)'Y' or 0x0D or 0x0A or 0x00))
                {
                    return (false, rx[0]);
                }
            }

            if (!OperatingSystem.IsWindows() && consecutiveZeroReads >= 4)
            {
                break;
            }
        }

        if (!got5F)
        {
            return (false, lastSeen);
        }

        // Complete remaining 3 sync bytes: 0x0A -> 0xF5, 0x50 -> 0xAF, 0x05 -> 0xFA
        byte[] restTx = [0x0A, 0x50, 0x05];
        byte[] restRx = [0xF5, 0xAF, 0xFA];

        for (int idx = 0; idx < 3; idx++)
        {
            byte sendByte = restTx[idx];
            byte wantByte = restRx[idx];
            await channel.WriteAsync(new byte[] { sendByte }, ct).ConfigureAwait(false);

            bool matched = false;
            for (int retry = 0; retry < 14; retry++)
            {
                int n = await channel.ReadAsync(rx, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    continue;
                }

                lastSeen = rx[0];
                if (rx[0] == wantByte)
                {
                    matched = true;
                    break;
                }

                if (idx == 0 && rx[0] == 0x5F)
                {
                    // Duplicate 0x5F from an earlier buffered 0xA0; keep reading for 0xF5
                    continue;
                }

                return (false, rx[0]);
            }

            if (!matched)
            {
                return (false, lastSeen);
            }
        }

        return (true, 0xFA);
    }

    private static async Task<bool> PerformDeterministicFourByteSyncAsync(ISerialByteChannel channel, CancellationToken ct)
    {
        byte[] seqTx = [0xA0, 0x0A, 0x50, 0x05];
        byte[] seqRx = [0x5F, 0xF5, 0xAF, 0xFA];
        var rx = new byte[1];

        for (int i = 0; i < 4; i++)
        {
            await channel.WriteAsync(new byte[] { seqTx[i] }, ct).ConfigureAwait(false);
            bool got = false;
            for (int retry = 0; retry < 12; retry++)
            {
                int n = await channel.ReadAsync(rx, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    continue;
                }

                if (rx[0] == seqRx[i])
                {
                    got = true;
                    break;
                }
            }

            if (!got)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<byte[]> ReadEchoedCommandFrameAsync(
        ISerialByteChannel channel,
        byte expectedEcho,
        int trailingBytes,
        CancellationToken ct)
    {
        var totalLen = 1 + trailingBytes;
        var result = new byte[totalLen];
        var one = new byte[1];

        // Find echoed command opcode (skipping any stray 0xFA/0x5F sync byte left in FIFO)
        bool foundEcho = false;
        for (int i = 0; i < 16; i++)
        {
            int n = await channel.ReadAsync(one, ct).ConfigureAwait(false);
            if (n <= 0)
            {
                continue;
            }

            if (one[0] == expectedEcho)
            {
                result[0] = one[0];
                foundEcho = true;
                break;
            }

            if (one[0] is not (0xFA or 0x5F or 0x00))
            {
                return [one[0]];
            }
        }

        if (!foundEcho)
        {
            return Array.Empty<byte>();
        }

        int offset = 1;
        int emptyReads = 0;
        while (offset < totalLen && emptyReads < 12)
        {
            int n = await channel.ReadAsync(result.AsMemory(offset, totalLen - offset), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                emptyReads++;
                continue;
            }
            emptyReads = 0;
            offset += n;
        }

        return offset == totalLen ? result : result[..offset];
    }

    public string ResolveChipsetFamilyFromHwCode(ushort hwCode) => hwCode switch
    {
        0x0788 => "MediaTek MT6789 (Helio G99 / G99 Ultimate / G100)",
        0x0707 => "MediaTek MT6768 / MT6769Z (Helio G85 / G88 / G91)",
        0x0766 => "MediaTek MT6765 (Helio G35 / G36 / G37 / P35)",
        0x0717 => "MediaTek MT6761 (Helio A22 / A25)",
        0x0725 => "MediaTek MT6762 / MT6763 (Helio P22 / P23)",
        0x0789 => "MediaTek MT6781 (Helio G96)",
        0x0790 => "MediaTek MT6771 (Helio P60 / P70)",
        0x0813 => "MediaTek MT6785 (Helio G90T / G95)",
        0x0886 => "MediaTek MT6853 (Dimensity 720 / 800U)",
        0x0950 => "MediaTek MT6835 (Dimensity 6080 / 6100+ / 6300)",
        0x0959 => "MediaTek MT6833 / MT6833P (Dimensity 700 / 6020)",
        0x0989 => "MediaTek MT6877 (Dimensity 900 / 1080 / 7050)",
        0x0996 => "MediaTek MT6855 (Dimensity 7020 / 930)",
        0x1065 => "MediaTek MT6893 (Dimensity 1200 / 1300 / 8020)",
        0x1129 => "MediaTek MT6895 / MT6896 (Dimensity 8100 / 8200 Ultimate)",
        0x1195 => "MediaTek MT6886 (Dimensity 7200)",
        0x1228 => "MediaTek MT6878 (Dimensity 7300 / 7300X)",
        0x1237 => "MediaTek MT6897 (Dimensity 8300 / 8350)",
        0x0321 => "MediaTek MT6735 / MT6737",
        0x0335 => "MediaTek MT6739",
        0x6580 => "MediaTek MT6580",
        _ => $"MediaTek SoC (HW_CODE 0x{hwCode:X4})"
    };

    public void Dispose() => ReleaseHeldPort();
}
