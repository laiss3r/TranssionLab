using System.Runtime.InteropServices;
using System.Text;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Usb;

namespace TranssionLab.Protocols.Unisoc;

/// <summary>
/// Spreadtrum / Unisoc Download &amp; Diagnostic protocol provider (Section 2C).
/// Implements documented HDLC 0x7E framing, byte-stuffing (0x7D escape), both CRC-16/XMODEM and
/// Spreadtrum BootROM IP-style checksum verification (supporting SC7731E/SC9863A/T603/T606/T612/T616/T620),
/// non-destructive BSL_CMD_CHECK_BAUD -> BSL_REP_VER (0x81) identification, and active Windows VCOM session hold.
/// </summary>
public sealed class SpdUnisocProvider : ISpdUnisocProvider, IDisposable
{
    public const ushort BslRepVer = 0x0081;
    public const byte HdlcFlag = 0x7E;
    public const byte HdlcEscape = 0x7D;

    private readonly ISerialChannelFactory _serialFactory;
    private readonly IStructuredLogger _logger;
    private readonly object _holdGate = new();
    private ISerialByteChannel? _heldChannel;
    private CancellationTokenSource? _holdCts;
    private string? _heldPortName;

    public string ProviderName => "Unisoc / Spreadtrum Protocol";
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

    public SpdUnisocProvider(ISerialChannelFactory serialFactory, IStructuredLogger logger)
    {
        _serialFactory = serialFactory;
        _logger = logger;
    }

    public IReadOnlyList<DetectedUsbDevice> FilterUnisocInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory) =>
        usbInventory
            .Where(u => u.VendorId == UsbSignatureCatalog.VidUnisoc ||
                        u.ClassifiedMode is ConnectionMode.UnisocDownload or ConnectionMode.UnisocDiag)
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

    public async Task<UnisocBootProbeResult> ProbeReadOnlyDownloadVersionAsync(string comPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comPort);
        var cleanPort = PlatformToolLocator.NormalizeSerialPortName(comPort);
        ReleaseHeldPort();

        ISerialByteChannel? channel = null;
        bool transferredToHold = false;

        try
        {
            channel = _serialFactory.Create(cleanPort, 115200, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(250));
            channel.Open();

            // Rapid BSL_CMD_CHECK_BAUD (0x7E) catch-and-hold loop so Unisoc Boot ROM (1782:4D00 / SPRD U2S Diag)
            // stays in Download mode instead of falling back to battery charging.
            var rxBuf = new byte[512];
            int total = 0;
            int maxSyncCycles = OperatingSystem.IsWindows() ? 70 : 8;

            for (int cycle = 0; cycle < maxSyncCycles; cycle++)
            {
                ct.ThrowIfCancellationRequested();
                if (total == 0)
                {
                    await channel.WriteAsync(new byte[] { HdlcFlag }, ct).ConfigureAwait(false);
                }

                for (int readStep = 0; readStep < 8 && total < rxBuf.Length; readStep++)
                {
                    int n = await channel.ReadAsync(rxBuf.AsMemory(total, rxBuf.Length - total), ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    total += n;
                    if (total >= 8 && TryDecodeHdlcFrame(rxBuf.AsSpan(0, total), out var earlyPkt, out _) && earlyPkt == BslRepVer)
                    {
                        break;
                    }
                }

                if (total >= 8 && TryDecodeHdlcFrame(rxBuf.AsSpan(0, total), out var checkPkt, out _) && checkPkt == BslRepVer)
                {
                    break;
                }

                if (!OperatingSystem.IsWindows() && total == 0 && cycle >= 2)
                {
                    break;
                }
            }

            if (total >= 8 && TryDecodeHdlcFrame(rxBuf.AsSpan(0, total), out var pktType, out var payload) && pktType == BslRepVer)
            {
                var banner = Encoding.ASCII.GetString(payload).TrimEnd('\0', '\r', '\n', ' ');

                if (OperatingSystem.IsWindows() && _serialFactory is SystemSerialChannelFactory)
                {
                    HoldWindowsUnisocSessionActive(cleanPort, channel);
                    transferredToHold = true;
                }

                var holdNote = transferredToHold ? " [VCOM HELD ACTIVE — charging blocked]" : string.Empty;
                var diag = $"Caught & held Unisoc Boot ROM on {cleanPort} — BSL_REP_VER: '{banner}'{holdNote}. Secure Boot Unisoc devices (T603/T606/T612/T616/T620) require OEM-signed FDL1 and FDL2 binaries inside a validated .pac package before RAM/flash download.";

                _logger.Log(new StructuredLogEntry
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    SessionId = _logger.CurrentSessionId,
                    SequenceNumber = 0,
                    Severity = LogSeverity.Info,
                    DeviceId = cleanPort,
                    ProtocolProvider = ProviderName,
                    UsbInterface = cleanPort,
                    OperationId = "unisoc.boot_version_probe",
                    RequestedAction = $"Unisoc HDLC 0x7E catch-and-hold probe on {cleanPort}",
                    CommandOutput = $"BSL_REP_VER='{banner}'",
                    VerificationResult = diag
                });

                return new UnisocBootProbeResult(
                    PortOpened: true,
                    CheckBaudAckReceived: true,
                    BootRomBannerVersion: banner,
                    RequiresSignedFdl: true,
                    ResultingState: ConnectionDashboardState.AuthenticationRequired,
                    DiagnosticMessage: diag,
                    PortHeldActive: transferredToHold);
            }

            var noFrameMsg = $"Opened {cleanPort}, but did not receive a valid HDLC BSL_REP_VER (0x81) frame (read {total} bytes). Power off phone completely, hold Vol- (or Vol+ & Vol-), and reconnect USB.";
            _logger.LogWarning(ProviderName, $"Unisoc Probe ({cleanPort})", noFrameMsg, cleanPort, "unisoc.boot_version_probe");
            return new UnisocBootProbeResult(true, false, null, true, ConnectionDashboardState.DetectedButUnidentified, noFrameMsg);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ProviderName, $"Unisoc Probe ({cleanPort})", ex.Message, null, cleanPort, "unisoc.boot_version_probe");
            return new UnisocBootProbeResult(false, false, null, true, ConnectionDashboardState.OperationFailed, ex.Message);
        }
        finally
        {
            if (!transferredToHold)
            {
                try { channel?.Dispose(); } catch { }
            }
        }
    }

    private void HoldWindowsUnisocSessionActive(string portName, ISerialByteChannel channel)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        lock (_holdGate)
        {
            _heldChannel = channel;
            if (_holdCts is not null)
            {
                try { _holdCts.Cancel(); } catch { }
                try { _holdCts.Dispose(); } catch { }
            }
            _holdCts = cts;
            _heldPortName = portName;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested && channel.IsOpen)
                {
                    await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Released or cancelled
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

    public byte[] EncodeHdlcFrame(ushort packetType, ReadOnlySpan<byte> payload)
    {
        var len = (ushort)payload.Length;
        var raw = new byte[4 + payload.Length + 2];
        raw[0] = (byte)(packetType >> 8);
        raw[1] = (byte)(packetType & 0xFF);
        raw[2] = (byte)(len >> 8);
        raw[3] = (byte)(len & 0xFF);
        payload.CopyTo(raw.AsSpan(4));

        ushort crc = ComputeCrc16Xmodem(raw.AsSpan(0, 4 + payload.Length));
        raw[4 + payload.Length] = (byte)(crc >> 8);
        raw[4 + payload.Length + 1] = (byte)(crc & 0xFF);

        var encoded = new List<byte>(raw.Length + 8) { HdlcFlag };
        foreach (var b in raw)
        {
            if (b is HdlcFlag or HdlcEscape)
            {
                encoded.Add(HdlcEscape);
                encoded.Add((byte)(b ^ 0x20));
            }
            else
            {
                encoded.Add(b);
            }
        }
        encoded.Add(HdlcFlag);
        return encoded.ToArray();
    }

    public bool TryDecodeHdlcFrame(ReadOnlySpan<byte> frame, out ushort packetType, out byte[] payload)
    {
        packetType = 0;
        payload = Array.Empty<byte>();

        int start = frame.IndexOf(HdlcFlag);
        if (start < 0 || start + 1 >= frame.Length)
            return false;

        // Skip leading 0x7E sync bytes
        while (start + 1 < frame.Length && frame[start + 1] == HdlcFlag)
            start++;

        int end = frame[(start + 1)..].IndexOf(HdlcFlag);
        if (end < 0)
            return false;
        end += start + 1;

        var unescaped = new List<byte>(end - start);
        for (int i = start + 1; i < end; i++)
        {
            byte b = frame[i];
            if (b == HdlcEscape)
            {
                if (i + 1 >= end) return false;
                unescaped.Add((byte)(frame[++i] ^ 0x20));
            }
            else
            {
                unescaped.Add(b);
            }
        }

        if (unescaped.Count < 6)
            return false;

        var span = CollectionsMarshal.AsSpan(unescaped);
        packetType = (ushort)((span[0] << 8) | span[1]);
        ushort declaredLen = (ushort)((span[2] << 8) | span[3]);
        if (4 + declaredLen + 2 != span.Length)
            return false;

        ushort expectedCrc = (ushort)((span[^2] << 8) | span[^1]);
        var bodySpan = span[..^2];
        ushort actualXmodem = ComputeCrc16Xmodem(bodySpan);
        ushort actualBootRomBe = ComputeSprdBootRomChecksum(bodySpan, littleEndianWords: false);
        ushort actualBootRomLe = ComputeSprdBootRomChecksum(bodySpan, littleEndianWords: true);

        if (expectedCrc != actualXmodem && expectedCrc != actualBootRomBe && expectedCrc != actualBootRomLe)
            return false;

        payload = span.Slice(4, declaredLen).ToArray();
        return true;
    }

    public static ushort ComputeCrc16Xmodem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    public static ushort ComputeSprdBootRomChecksum(ReadOnlySpan<byte> data, bool littleEndianWords)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
        {
            sum += littleEndianWords
                ? (uint)(data[i] | (data[i + 1] << 8))
                : (uint)((data[i] << 8) | data[i + 1]);
        }

        if (i < data.Length)
        {
            sum += littleEndianWords ? data[i] : (uint)(data[i] << 8);
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        ushort inv = (ushort)~sum;
        return littleEndianWords ? (ushort)((inv >> 8) | (inv << 8)) : inv;
    }

    public void Dispose() => ReleaseHeldPort();
}
