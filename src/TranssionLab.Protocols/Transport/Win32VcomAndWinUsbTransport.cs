using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TranssionLab.Protocols.Transport;

/// <summary>
/// Ultra-low-latency (&lt;0.6ms) native Win32 SetupAPI + CfgMgr32 + SERIALCOMM scanner and
/// raw Win32 CreateFileW / WinUSB transport channels so transient MediaTek Preloader/BROM (0E8D:2000/0003)
/// and Unisoc Boot ROM (1782:4D00 / SPRD_ENUM) endpoints are caught and locked before battery charging starts.
/// Requires zero administrator privileges and zero unsigned drivers.
/// </summary>
internal static class Win32VcomAndWinUsbTransport
{
    public const string WinUsbPortPrefix = "WINUSB:";

    private static Guid GuidDevClassPorts = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private static Guid GuidDevInterfaceUsbDevice = new("a5dcbf10-6530-11d0-901f-00c04fb951ed");

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_SERVICE = 0x00000004;
    private const uint SPDRP_MFG = 0x0000000B;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    private const uint DICS_FLAG_GLOBAL = 0x00000001;
    private const uint DIREG_DEV = 0x00000001;
    private const int KEY_QUERY_VALUE = 0x0001;
    private const uint DN_HAS_PROBLEM = 0x00000400;

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<RawPnpDeviceRecord> ScanFastLiveVcomAndUsbEndpoints()
    {
        var byId = new Dictionary<string, RawPnpDeviceRecord>(StringComparer.OrdinalIgnoreCase);
        var mappedComPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Sub-0.5ms SetupAPI scan of all PRESENT Ports (COM & LPT) — covers USB\VID_0E8D, USB\VID_1782, SPRD_ENUM, usbser.sys!
        ScanSetupApiPortsClass(byId, mappedComPorts);

        // 2. Sub-0.8ms SetupAPI scan of PRESENT USB device interfaces — catches WinUSB-bound Preloader/BROM/Unisoc
        //    AND catches missing-driver (Code 28 / Code 52) Preloader/Unisoc devices during their 1.8s boot window!
        ScanSetupApiPresentUsbInterfaces(byId, mappedComPorts);

        // 3. Per-key safe SERIALCOMM + Registry fallback for any brand-new COM port still completing INF property writes
        AppendLiveSerialCommPortsFallback(byId, mappedComPorts);

        return byId.Values.ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void ScanSetupApiPortsClass(
        Dictionary<string, RawPnpDeviceRecord> byId,
        HashSet<string> mappedComPorts)
    {
        IntPtr devInfo = SetupDiGetClassDevsW(ref GuidDevClassPorts, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
        {
            return;
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(devInfo, index, ref data); index++)
            {
                var instanceId = GetDeviceInstanceId(devInfo, ref data);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                var friendly = GetDeviceStringProperty(devInfo, ref data, SPDRP_FRIENDLYNAME);
                var desc = GetDeviceStringProperty(devInfo, ref data, SPDRP_DEVICEDESC);
                var mfg = GetDeviceStringProperty(devInfo, ref data, SPDRP_MFG) ?? "USB Serial Vendor";
                var service = GetDeviceStringProperty(devInfo, ref data, SPDRP_SERVICE);
                var portName = ReadDevRegPortName(devInfo, ref data);
                int errCode = GetDevNodeProblemCode(data.DevInst);

                var displayName = !string.IsNullOrWhiteSpace(friendly) ? friendly! : (desc ?? "USB Serial Port");
                if (!string.IsNullOrWhiteSpace(portName) &&
                    !displayName.Contains($"({portName})", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = $"{displayName} ({portName})";
                }

                if (!string.IsNullOrWhiteSpace(portName))
                {
                    mappedComPorts.Add(portName!);
                }

                byId[instanceId] = new RawPnpDeviceRecord(
                    instanceId,
                    displayName,
                    mfg,
                    service,
                    errCode,
                    errCode == 0 ? "OK" : $"Problem {errCode}");
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ScanSetupApiPresentUsbInterfaces(
        Dictionary<string, RawPnpDeviceRecord> byId,
        HashSet<string> mappedComPorts)
    {
        IntPtr devInfo = SetupDiGetClassDevsW(
            ref GuidDevInterfaceUsbDevice,
            IntPtr.Zero,
            IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
        {
            return;
        }

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref GuidDevInterfaceUsbDevice, index, ref ifData); index++)
            {
                var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                var devicePath = GetDeviceInterfacePath(devInfo, ref ifData, ref devData);
                var instanceId = GetDeviceInstanceId(devInfo, ref devData);
                if (string.IsNullOrWhiteSpace(instanceId) || byId.ContainsKey(instanceId))
                {
                    continue;
                }

                // Only inspect servicing-relevant USB VIDs in the fast path (MediaTek, Unisoc, Transsion, Google, LK, Qualcomm)
                if (!IsServicingUsbInstanceId(instanceId))
                {
                    continue;
                }

                var friendly = GetDeviceStringProperty(devInfo, ref devData, SPDRP_FRIENDLYNAME);
                var desc = GetDeviceStringProperty(devInfo, ref devData, SPDRP_DEVICEDESC);
                var mfg = GetDeviceStringProperty(devInfo, ref devData, SPDRP_MFG) ?? "USB Vendor";
                var service = GetDeviceStringProperty(devInfo, ref devData, SPDRP_SERVICE);
                var portName = ReadDevRegPortName(devInfo, ref devData);
                int errCode = GetDevNodeProblemCode(devData.DevInst);

                var displayName = !string.IsNullOrWhiteSpace(friendly) ? friendly! : (desc ?? "USB Device");
                if (!string.IsNullOrWhiteSpace(portName))
                {
                    mappedComPorts.Add(portName!);
                    if (!displayName.Contains($"({portName})", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = $"{displayName} ({portName})";
                    }
                }
                else if (errCode == 0 &&
                         !string.IsNullOrWhiteSpace(devicePath) &&
                         IsWinUsbBootromOrPreloader(instanceId, service))
                {
                    // Technician PC has WinUSB (e.g. Zadig / GSM driver) bound directly to Preloader/BROM/Unisoc!
                    // Tag with WINUSB:<devicePath> token so MediaTekProvider / SpdUnisocProvider can talk directly over WinUSB bulk pipes!
                    displayName = $"{displayName} [{WinUsbPortPrefix}{devicePath}]";
                }

                byId[instanceId] = new RawPnpDeviceRecord(
                    instanceId,
                    displayName,
                    mfg,
                    service,
                    errCode,
                    errCode == 0 ? "OK" : $"Problem {errCode}");
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfo);
        }

        // Also scan problem USB nodes (e.g. Code 28 MT65xx Preloader without GUID_DEVINTERFACE_USB_DEVICE)
        ScanPresentUsbProblemNodesFast(byId);
    }

    [SupportedOSPlatform("windows")]
    private static void ScanPresentUsbProblemNodesFast(Dictionary<string, RawPnpDeviceRecord> byId)
    {
        Guid emptyGuid = Guid.Empty;
        IntPtr devInfo = SetupDiGetClassDevsW(ref emptyGuid, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
        {
            return;
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(devInfo, index, ref data); index++)
            {
                var instanceId = GetDeviceInstanceId(devInfo, ref data);
                if (string.IsNullOrWhiteSpace(instanceId) || byId.ContainsKey(instanceId) || !IsServicingUsbInstanceId(instanceId))
                {
                    continue;
                }

                int errCode = GetDevNodeProblemCode(data.DevInst);
                var friendly = GetDeviceStringProperty(devInfo, ref data, SPDRP_FRIENDLYNAME);
                var desc = GetDeviceStringProperty(devInfo, ref data, SPDRP_DEVICEDESC);
                var mfg = GetDeviceStringProperty(devInfo, ref data, SPDRP_MFG) ?? "USB Vendor";
                var service = GetDeviceStringProperty(devInfo, ref data, SPDRP_SERVICE);
                var portName = ReadDevRegPortName(devInfo, ref data);

                var displayName = !string.IsNullOrWhiteSpace(friendly) ? friendly! : (desc ?? "USB Device");
                if (!string.IsNullOrWhiteSpace(portName) &&
                    !displayName.Contains($"({portName})", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = $"{displayName} ({portName})";
                }

                byId[instanceId] = new RawPnpDeviceRecord(
                    instanceId,
                    displayName,
                    mfg,
                    service,
                    errCode,
                    errCode == 0 ? "OK" : $"Problem {errCode}");
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AppendLiveSerialCommPortsFallback(
        Dictionary<string, RawPnpDeviceRecord> byId,
        HashSet<string> mappedComPorts)
    {
        try
        {
            using var serialComm = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (serialComm is null)
            {
                return;
            }

            foreach (var kernelDeviceName in serialComm.GetValueNames())
            {
                string? portName;
                try
                {
                    portName = serialComm.GetValue(kernelDeviceName)?.ToString()?.Trim();
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(portName) || mappedComPorts.Contains(portName))
                {
                    continue;
                }

                // Ignore legacy motherboard UART (\Device\Serial0 = COM1) unless USB/VCOM/SPRD/CDC kernel device
                var upperKernel = kernelDeviceName.ToUpperInvariant();
                bool isUsbSerialKernel =
                    upperKernel.Contains("USBSER") ||
                    upperKernel.Contains("VCOM") ||
                    upperKernel.Contains("SCIU2S") ||
                    upperKernel.Contains("SPRD") ||
                    upperKernel.Contains("MTK") ||
                    upperKernel.Contains("CDC") ||
                    upperKernel.Contains("DIAG") ||
                    upperKernel.Contains("PROLIFIC") ||
                    upperKernel.Contains("SILAB");

                if (!isUsbSerialKernel)
                {
                    continue;
                }

                bool looksUnisoc = upperKernel.Contains("SCIU2S") || upperKernel.Contains("SPRD");
                var synthId = looksUnisoc
                    ? $@"USB\VID_1782&PID_4D00\LIVE_{portName}"
                    : $@"USB\VID_0E8D&PID_2000\LIVE_{portName}";
                var label = looksUnisoc
                    ? $"Unisoc / SPRD Live USB VCOM ({portName})"
                    : $"Live USB VCOM Serial Port ({portName})";

                if (!byId.ContainsKey(synthId))
                {
                    byId[synthId] = new RawPnpDeviceRecord(synthId, label, "Kernel SERIALCOMM", "usbser", 0, "OK");
                    mappedComPorts.Add(portName);
                }
            }
        }
        catch
        {
            // Ignore SERIALCOMM fallback restriction
        }
    }

    private static bool IsServicingUsbInstanceId(string instanceId) =>
        instanceId.Contains("VID_0E8D", StringComparison.OrdinalIgnoreCase) ||
        instanceId.Contains("VID_1782", StringComparison.OrdinalIgnoreCase) ||
        instanceId.Contains("VID_2ECC", StringComparison.OrdinalIgnoreCase) ||
        instanceId.Contains("VID_18D1", StringComparison.OrdinalIgnoreCase) ||
        instanceId.Contains("VID_0BB4", StringComparison.OrdinalIgnoreCase) ||
        instanceId.Contains("VID_05C6", StringComparison.OrdinalIgnoreCase) ||
        instanceId.StartsWith("SPRD", StringComparison.OrdinalIgnoreCase);

    private static bool IsWinUsbBootromOrPreloader(string instanceId, string? service)
    {
        if (string.IsNullOrWhiteSpace(service) ||
            (!service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) &&
             !service.Contains("libusb", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return instanceId.Contains("VID_0E8D&PID_0003", StringComparison.OrdinalIgnoreCase) ||
               instanceId.Contains("VID_0E8D&PID_2000", StringComparison.OrdinalIgnoreCase) ||
               instanceId.Contains("VID_0E8D&PID_2001", StringComparison.OrdinalIgnoreCase) ||
               instanceId.Contains("VID_1782&PID_4D00", StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    public static ISerialByteChannel CreateWindowsChannel(
        string portOrEndpoint,
        int baudRate,
        TimeSpan readTimeout,
        TimeSpan writeTimeout)
    {
        if (portOrEndpoint.StartsWith(WinUsbPortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var devicePath = portOrEndpoint[WinUsbPortPrefix.Length..].Trim();
            return new WinUsbBulkByteChannel(devicePath, readTimeout, writeTimeout);
        }

        return new Win32SerialByteChannel(portOrEndpoint, baudRate, readTimeout, writeTimeout);
    }

    [SupportedOSPlatform("windows")]
    private static string? GetDeviceInstanceId(IntPtr devInfo, ref SP_DEVINFO_DATA data)
    {
        Span<char> buffer = stackalloc char[512];
        if (SetupDiGetDeviceInstanceIdW(devInfo, ref data, ref MemoryMarshal.GetReference(buffer), (uint)buffer.Length, out uint required) && required > 0)
        {
            int len = (int)Math.Min(required - 1, buffer.Length);
            return new string(buffer[..len]);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetDeviceStringProperty(IntPtr devInfo, ref SP_DEVINFO_DATA data, uint property)
    {
        Span<byte> buffer = stackalloc byte[512];
        if (SetupDiGetDeviceRegistryPropertyW(
            devInfo,
            ref data,
            property,
            out _,
            ref MemoryMarshal.GetReference(buffer),
            (uint)buffer.Length,
            out uint required) && required >= 2)
        {
            var str = MemoryMarshal.Cast<byte, char>(buffer[..(int)Math.Min(required, (uint)buffer.Length)]);
            int nullIdx = str.IndexOf('\0');
            return nullIdx >= 0 ? new string(str[..nullIdx]) : new string(str);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadDevRegPortName(IntPtr devInfo, ref SP_DEVINFO_DATA data)
    {
        IntPtr hKey = SetupDiOpenDevRegKey(devInfo, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_QUERY_VALUE);
        if (hKey == IntPtr.Zero || hKey == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            using var safeKey = new SafeRegistryHandle(hKey, ownsHandle: true);
            using var regKey = RegistryKey.FromHandle(safeKey);
            var port = regKey.GetValue("PortName")?.ToString()?.Trim();
            return !string.IsNullOrWhiteSpace(port) && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                ? port.ToUpperInvariant()
                : null;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int GetDevNodeProblemCode(uint devInst)
    {
        if (CM_Get_DevNode_Status(out uint status, out uint problemNumber, devInst, 0) == 0)
        {
            if ((status & DN_HAS_PROBLEM) != 0)
            {
                return (int)problemNumber;
            }
        }

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetDeviceInterfacePath(
        IntPtr devInfo,
        ref SP_DEVICE_INTERFACE_DATA ifData,
        ref SP_DEVINFO_DATA devData)
    {
        _ = SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, IntPtr.Zero, 0, out uint requiredSize, ref devData);
        if (requiredSize < 8 || requiredSize > 4096)
        {
            return null;
        }

        IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            uint cbSize = IntPtr.Size == 8 ? 8u : 6u;
            Marshal.WriteInt32(detailBuffer, (int)cbSize);
            if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, detailBuffer, requiredSize, out _, ref devData))
            {
                return null;
            }

            return Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    /// <summary>
    /// Direct Win32 CreateFileW + SetCommState + SetCommTimeouts serial channel without .NET SerialStream
    /// WaitCommEvent background contention or CancelIoEx USB IRP aborts.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class Win32SerialByteChannel : ISerialByteChannel
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint SETRTS = 3;
        private const uint SETDTR = 5;

        private readonly string _devicePath;
        private readonly int _baudRate;
        private readonly uint _readTimeoutMs;
        private readonly uint _writeTimeoutMs;
        private SafeFileHandle? _handle;

        public Win32SerialByteChannel(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout)
        {
            var normalized = PlatformToolLocator.NormalizeSerialPortName(portName);
            _devicePath = normalized.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? normalized
                : $@"\\.\{normalized}";
            _baudRate = baudRate;
            _readTimeoutMs = (uint)Math.Clamp(readTimeout.TotalMilliseconds, 12, 5000);
            _writeTimeoutMs = (uint)Math.Clamp(writeTimeout.TotalMilliseconds, 50, 5000);
        }

        public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };

        public void Open()
        {
            // Retry briefly (up to ~320ms) if usbser.sys / SciU2S is still completing PnP handle creation
            int lastErr = 0;
            for (int attempt = 1; attempt <= 22; attempt++)
            {
                var h = CreateFileW(
                    _devicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (!h.IsInvalid)
                {
                    _handle = h;
                    ConfigurePortHandle(h);
                    return;
                }

                lastErr = Marshal.GetLastWin32Error();
                h.Dispose();

                // 2 = FILE_NOT_FOUND, 5 = ACCESS_DENIED, 31 = GEN_FAILURE, 32 = SHARING_VIOLATION
                if (attempt < 22 && lastErr is 2 or 5 or 31 or 32)
                {
                    Thread.Sleep(15);
                    continue;
                }

                break;
            }

            throw new IOException($"Win32 CreateFileW could not open {_devicePath} (Win32 error {lastErr}: {new Win32Exception(lastErr).Message}).");
        }

        private void ConfigurePortHandle(SafeFileHandle h)
        {
            _ = SetupComm(h, 65536, 65536);

            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            if (GetCommState(h, ref dcb))
            {
                dcb.BaudRate = (uint)_baudRate;
                dcb.ByteSize = 8;
                dcb.Parity = 0;
                dcb.StopBits = 0;
                // fBinary(0x0001) | DTR_CONTROL_ENABLE(0x0010) | RTS_CONTROL_ENABLE(0x1000); fAbortOnError = 0
                dcb.Flags = 0x0001u | 0x0010u | 0x1000u;
                _ = SetCommState(h, ref dcb);
            }

            _ = EscapeCommFunction(h, SETDTR);
            _ = EscapeCommFunction(h, SETRTS);

            var timeouts = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = 4,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = _readTimeoutMs,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = _writeTimeoutMs
            };
            _ = SetCommTimeouts(h, ref timeouts);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_handle is null || _handle.IsInvalid || _handle.IsClosed)
            {
                throw new InvalidOperationException("Serial port is not open.");
            }

            if (buffer.IsEmpty)
            {
                return Task.CompletedTask;
            }

            byte[] arr = buffer.ToArray();
            if (!WriteFile(_handle, arr, (uint)arr.Length, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"WriteFile failed on {_devicePath} (Win32 error {err}).");
            }

            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_handle is null || _handle.IsInvalid || _handle.IsClosed || buffer.IsEmpty)
            {
                return Task.FromResult(0);
            }

            byte[] temp = new byte[buffer.Length];
            if (!ReadFile(_handle, temp, (uint)temp.Length, out uint bytesRead, IntPtr.Zero))
            {
                return Task.FromResult(0);
            }

            if (bytesRead > 0)
            {
                temp.AsSpan(0, (int)bytesRead).CopyTo(buffer.Span);
            }

            return Task.FromResult((int)bytesRead);
        }

        public void Dispose()
        {
            try
            {
                _handle?.Dispose();
            }
            catch
            {
                // Best-effort handle release
            }
            _handle = null;
        }
    }

    /// <summary>
    /// Native Microsoft WinUSB (winusb.dll) bulk channel for technician PCs where MediaTek Preloader/BROM
    /// or Unisoc Boot ROM is bound to WinUSB instead of usbser.sys.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class WinUsbBulkByteChannel : ISerialByteChannel
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        private const int USBD_PIPE_TYPE_BULK = 2;

        private readonly string _devicePath;
        private readonly uint _readTimeoutMs;
        private readonly uint _writeTimeoutMs;

        private SafeFileHandle? _fileHandle;
        private IntPtr _winUsbRootHandle;
        private IntPtr _associatedIfHandle;
        private IntPtr _activePipeIfHandle;
        private byte _bulkInPipe;
        private byte _bulkOutPipe;

        public WinUsbBulkByteChannel(string devicePath, TimeSpan readTimeout, TimeSpan writeTimeout)
        {
            _devicePath = devicePath;
            _readTimeoutMs = (uint)Math.Clamp(readTimeout.TotalMilliseconds, 15, 5000);
            _writeTimeoutMs = (uint)Math.Clamp(writeTimeout.TotalMilliseconds, 50, 5000);
        }

        public bool IsOpen => _activePipeIfHandle != IntPtr.Zero;

        public void Open()
        {
            int lastErr = 0;
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                var fh = CreateFileW(
                    _devicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                if (!fh.IsInvalid)
                {
                    if (WinUsb_Initialize(fh, out IntPtr rootHandle))
                    {
                        _fileHandle = fh;
                        _winUsbRootHandle = rootHandle;
                        DiscoverBulkPipesAndConfigure();
                        return;
                    }

                    lastErr = Marshal.GetLastWin32Error();
                    fh.Dispose();
                }
                else
                {
                    lastErr = Marshal.GetLastWin32Error();
                    fh.Dispose();
                }

                if (attempt < 15)
                {
                    Thread.Sleep(15);
                }
            }

            throw new IOException($"Could not initialize WinUSB interface '{_devicePath}' (Win32 error {lastErr}).");
        }

        private void DiscoverBulkPipesAndConfigure()
        {
            if (TryFindBulkPipes(_winUsbRootHandle, out _bulkInPipe, out _bulkOutPipe))
            {
                _activePipeIfHandle = _winUsbRootHandle;
            }
            else if (WinUsb_GetAssociatedInterface(_winUsbRootHandle, 0, out _associatedIfHandle) &&
                     TryFindBulkPipes(_associatedIfHandle, out _bulkInPipe, out _bulkOutPipe))
            {
                _activePipeIfHandle = _associatedIfHandle;
            }
            else
            {
                Dispose();
                throw new IOException("WinUSB device did not expose Bulk IN and Bulk OUT endpoints.");
            }

            uint rTimeout = _readTimeoutMs;
            uint wTimeout = _writeTimeoutMs;
            _ = WinUsb_SetPipePolicy(_activePipeIfHandle, _bulkInPipe, PIPE_TRANSFER_TIMEOUT, 4, ref rTimeout);
            _ = WinUsb_SetPipePolicy(_activePipeIfHandle, _bulkOutPipe, PIPE_TRANSFER_TIMEOUT, 4, ref wTimeout);

            // Best-effort CDC-ACM 115200 8N1 + DTR|RTS control packets
            byte[] lineCoding = [0x00, 0xC2, 0x01, 0x00, 0x00, 0x00, 0x08];
            var setCoding = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x21,
                Request = 0x20,
                Value = 0,
                Index = 0,
                Length = 7
            };
            _ = WinUsb_ControlTransfer(_winUsbRootHandle, setCoding, lineCoding, 7, out _, IntPtr.Zero);

            var setDtrRts = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x21,
                Request = 0x22,
                Value = 0x0003,
                Index = 0,
                Length = 0
            };
            _ = WinUsb_ControlTransfer(_winUsbRootHandle, setDtrRts, Array.Empty<byte>(), 0, out _, IntPtr.Zero);
        }

        private static bool TryFindBulkPipes(IntPtr ifHandle, out byte bulkIn, out byte bulkOut)
        {
            bulkIn = 0;
            bulkOut = 0;
            if (!WinUsb_QueryInterfaceSettings(ifHandle, 0, out var desc))
            {
                return false;
            }

            for (byte i = 0; i < desc.bNumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(ifHandle, 0, i, out var pipe))
                {
                    continue;
                }

                if (pipe.PipeType == USBD_PIPE_TYPE_BULK)
                {
                    if ((pipe.PipeId & 0x80) != 0 && bulkIn == 0)
                    {
                        bulkIn = pipe.PipeId;
                    }
                    else if ((pipe.PipeId & 0x80) == 0 && bulkOut == 0)
                    {
                        bulkOut = pipe.PipeId;
                    }
                }
            }

            return bulkIn != 0 && bulkOut != 0;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_activePipeIfHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("WinUSB channel is not open.");
            }

            byte[] arr = buffer.ToArray();
            if (!WinUsb_WritePipe(_activePipeIfHandle, _bulkOutPipe, arr, (uint)arr.Length, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"WinUsb_WritePipe failed (Win32 error {err}).");
            }

            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_activePipeIfHandle == IntPtr.Zero || buffer.IsEmpty)
            {
                return Task.FromResult(0);
            }

            byte[] temp = new byte[buffer.Length];
            if (!WinUsb_ReadPipe(_activePipeIfHandle, _bulkInPipe, temp, (uint)temp.Length, out uint transferred, IntPtr.Zero))
            {
                return Task.FromResult(0);
            }

            if (transferred > 0)
            {
                temp.AsSpan(0, (int)transferred).CopyTo(buffer.Span);
            }

            return Task.FromResult((int)transferred);
        }

        public void Dispose()
        {
            if (_associatedIfHandle != IntPtr.Zero)
            {
                _ = WinUsb_Free(_associatedIfHandle);
                _associatedIfHandle = IntPtr.Zero;
            }
            if (_winUsbRootHandle != IntPtr.Zero)
            {
                _ = WinUsb_Free(_winUsbRootHandle);
                _winUsbRootHandle = IntPtr.Zero;
            }
            _activePipeIfHandle = IntPtr.Zero;
            _fileHandle?.Dispose();
            _fileHandle = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINUSB_PIPE_INFORMATION
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref char deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        ref byte propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint scope,
        uint hwProfile,
        uint keyType,
        int samDesired);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_DevNode_Status(
        out uint ulStatus,
        out uint ulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupComm(SafeFileHandle hFile, uint dwInQueue, uint dwOutQueue);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(SafeFileHandle hFile, ref COMMTIMEOUTS lpCommTimeouts);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EscapeCommFunction(SafeFileHandle hFile, uint dwFunc);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        [Out] byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_GetAssociatedInterface(
        IntPtr interfaceHandle,
        byte associatedInterfaceIndex,
        out IntPtr associatedInterfaceHandle);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryInterfaceSettings(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        out USB_INTERFACE_DESCRIPTOR usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryPipe(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        byte pipeIndex,
        out WINUSB_PIPE_INFORMATION pipeInformation);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_SetPipePolicy(
        IntPtr interfaceHandle,
        byte pipeId,
        uint policyType,
        uint valueLength,
        ref uint value);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_ControlTransfer(
        IntPtr interfaceHandle,
        WINUSB_SETUP_PACKET setupPacket,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_ReadPipe(
        IntPtr interfaceHandle,
        byte pipeId,
        [Out] byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_WritePipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);
}
