using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TranssionLab.Core.Interfaces;

namespace TranssionLab.Protocols.Transport;

public static partial class PlatformToolLocator
{
    [GeneratedRegex(@"(COM\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ComTokenRegex();

    public static string NormalizeSerialPortName(string rawPortOrLabel)
    {
        if (string.IsNullOrWhiteSpace(rawPortOrLabel))
        {
            return string.Empty;
        }

        var trimmed = rawPortOrLabel.Trim();
        if (trimmed.StartsWith(Win32VcomAndWinUsbTransport.WinUsbPortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // Extract bracketed [WINUSB:...] token if passed from a FriendlyName label
        var winUsbIdx = trimmed.IndexOf(Win32VcomAndWinUsbTransport.WinUsbPortPrefix, StringComparison.OrdinalIgnoreCase);
        if (winUsbIdx >= 0)
        {
            var endBracket = trimmed.IndexOf(']', winUsbIdx);
            return endBracket > winUsbIdx ? trimmed[winUsbIdx..endBracket] : trimmed[winUsbIdx..];
        }

        var match = ComTokenRegex().Match(trimmed);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : trimmed;
    }

    public static string ResolveToolExecutable(string? configuredSetting, string defaultCommandName, string? workspaceDataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredSetting) &&
            !string.Equals(configuredSetting.Trim(), defaultCommandName, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(configuredSetting.Trim()))
        {
            return configuredSetting.Trim();
        }

        var exeName = OperatingSystem.IsWindows() ? $"{defaultCommandName}.exe" : defaultCommandName;
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "platform-tools", exeName),
            Path.Combine(baseDir, "tools", exeName),
            Path.Combine(baseDir, exeName)
        };

        if (!string.IsNullOrWhiteSpace(workspaceDataDirectory))
        {
            candidates.Add(Path.Combine(workspaceDataDirectory, "platform-tools", exeName));
            candidates.Add(Path.Combine(workspaceDataDirectory, "tools", exeName));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "TranssionLab", "platform-tools", exeName));
            candidates.Add(Path.Combine(localAppData, "Programs", "TranssionLab", "platform-tools", exeName));
            candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", exeName));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredSetting) &&
            !string.Equals(configuredSetting.Trim(), defaultCommandName, StringComparison.OrdinalIgnoreCase))
        {
            return configuredSetting.Trim();
        }

        return defaultCommandName;
    }
}

public interface IProcessRunner
{
    Task<CommandExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<CommandExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new CommandExecutionResult(false, -1, string.Empty, $"Failed to start process '{executablePath}'.", sw.ElapsedMilliseconds);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                return new CommandExecutionResult(false, -2, string.Empty, $"Command '{executablePath}' timed out after {timeout.TotalSeconds:F1}s.", sw.ElapsedMilliseconds);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            return new CommandExecutionResult(
                process.ExitCode == 0,
                process.ExitCode,
                stdout,
                stderr,
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CommandExecutionResult(false, -99, string.Empty, ex.Message, sw.ElapsedMilliseconds);
        }
    }
}

public sealed record RawPnpDeviceRecord(
    string DeviceId,
    string Name,
    string Manufacturer,
    string? Service,
    int? ConfigManagerErrorCode,
    string? Status);

public interface IWindowsPnpBackend
{
    event EventHandler? DeviceTopologyChanged;
    Task<IReadOnlyList<RawPnpDeviceRecord>> QueryUsbAndPortEntitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RawPnpDeviceRecord>> QueryFastVcomEntitiesAsync(CancellationToken ct = default) =>
        QueryUsbAndPortEntitiesAsync(ct);
    IReadOnlyList<string> GetSystemComPortNames();
    void StartWatching();
    void StopWatching();
}

public sealed class WindowsWmiPnpBackend : IWindowsPnpBackend, IDisposable
{
    private ManagementEventWatcher? _arrivalWatcher;
    private ManagementEventWatcher? _removalWatcher;

    public event EventHandler? DeviceTopologyChanged;

    public Task<IReadOnlyList<RawPnpDeviceRecord>> QueryUsbAndPortEntitiesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<RawPnpDeviceRecord>>(Array.Empty<RawPnpDeviceRecord>());
        }

        return Task.Run(QueryWindowsCombinedInternal, ct);
    }

    public Task<IReadOnlyList<RawPnpDeviceRecord>> QueryFastVcomEntitiesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<RawPnpDeviceRecord>>(Array.Empty<RawPnpDeviceRecord>());
        }

        return Task.FromResult(Win32VcomAndWinUsbTransport.ScanFastLiveVcomAndUsbEndpoints());
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RawPnpDeviceRecord> QueryWindowsCombinedInternal()
    {
        var byDeviceId = new Dictionary<string, RawPnpDeviceRecord>(StringComparer.OrdinalIgnoreCase);

        // 1. Always merge sub-millisecond Win32 SetupAPI + CfgMgr32 + SERIALCOMM live endpoints first
        foreach (var fastRec in Win32VcomAndWinUsbTransport.ScanFastLiveVcomAndUsbEndpoints())
        {
            byDeviceId[fastRec.DeviceId] = fastRec;
        }

        // 2. Query WMI Win32_PnPEntity for WinUSB / ADB / Fastboot / MTP / Ports interfaces
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Manufacturer, Service, ConfigManagerErrorCode, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%' OR PNPClass = 'Ports'");
            using var collection = searcher.Get();
            foreach (var obj in collection)
            {
                if (obj is not ManagementObject mo)
                    continue;

                var deviceId = mo["DeviceID"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(deviceId))
                    continue;

                int? errCode = mo["ConfigManagerErrorCode"] is uint u ? (int)u :
                               mo["ConfigManagerErrorCode"] is int i ? i : null;

                // Skip disconnected phantom entries (Code 45)
                if (errCode == 45)
                    continue;

                if (byDeviceId.ContainsKey(deviceId))
                    continue;

                var name = mo["Name"]?.ToString() ?? "USB Device";
                var mfg = mo["Manufacturer"]?.ToString() ?? "Unknown";
                var service = mo["Service"]?.ToString();
                var status = mo["Status"]?.ToString();

                byDeviceId[deviceId] = new RawPnpDeviceRecord(deviceId, name, mfg, service, errCode, status);
            }
        }
        catch
        {
            // WMI unavailable or restricted; fast VCOM records still returned safely
        }

        return byDeviceId.Values.ToList();
    }

    public IReadOnlyList<string> GetSystemComPortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void StartWatching()
    {
        if (!OperatingSystem.IsWindows())
            return;

        StartWindowsWatchers();
    }

    [SupportedOSPlatform("windows")]
    private void StartWindowsWatchers()
    {
        try
        {
            StopWindowsWatchers();
            var arrivalQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity'");
            _arrivalWatcher = new ManagementEventWatcher(arrivalQuery);
            _arrivalWatcher.EventArrived += (_, _) => DeviceTopologyChanged?.Invoke(this, EventArgs.Empty);
            _arrivalWatcher.Start();

            var removalQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity'");
            _removalWatcher = new ManagementEventWatcher(removalQuery);
            _removalWatcher.EventArrived += (_, _) => DeviceTopologyChanged?.Invoke(this, EventArgs.Empty);
            _removalWatcher.Start();
        }
        catch
        {
            // Fall back cleanly to polling when WMI event subscription is restricted
        }
    }

    public void StopWatching()
    {
        if (OperatingSystem.IsWindows())
        {
            StopWindowsWatchers();
        }
    }

    [SupportedOSPlatform("windows")]
    private void StopWindowsWatchers()
    {
        try { _arrivalWatcher?.Stop(); _arrivalWatcher?.Dispose(); } catch { }
        try { _removalWatcher?.Stop(); _removalWatcher?.Dispose(); } catch { }
        _arrivalWatcher = null;
        _removalWatcher = null;
    }

    public void Dispose() => StopWatching();
}

public interface ISerialByteChannel : IDisposable
{
    bool IsOpen { get; }
    void Open();
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}

public interface ISerialChannelFactory
{
    ISerialByteChannel Create(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout);
}

public sealed class SystemSerialChannelFactory : ISerialChannelFactory
{
    public ISerialByteChannel Create(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout) =>
        OperatingSystem.IsWindows()
            ? Win32VcomAndWinUsbTransport.CreateWindowsChannel(portName, baudRate, readTimeout, writeTimeout)
            : new SystemSerialPortChannel(portName, baudRate, readTimeout, writeTimeout);

    private sealed class SystemSerialPortChannel : ISerialByteChannel
    {
        private readonly SerialPort _port;

        public SystemSerialPortChannel(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout)
        {
            var normalizedPort = PlatformToolLocator.NormalizeSerialPortName(portName);
            _port = new SerialPort(normalizedPort, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = (int)Math.Max(12, readTimeout.TotalMilliseconds),
                WriteTimeout = (int)Math.Max(50, writeTimeout.TotalMilliseconds),
                ReadBufferSize = 65536,
                WriteBufferSize = 65536,
                DtrEnable = true,
                RtsEnable = true
            };
        }

        public bool IsOpen => _port.IsOpen;

        public void Open()
        {
            // When Windows usbser.sys / SprdU2s first creates a VCOM node, PnP binding can briefly return
            // UnauthorizedAccessException for 10-60ms. Retry rapidly so we catch Preloader before charging!
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    _port.Open();
                    _port.DtrEnable = true;
                    _port.RtsEnable = true;
                    return;
                }
                catch (Exception ex) when (attempt < 15 && ex is UnauthorizedAccessException or IOException)
                {
                    Thread.Sleep(20);
                }
            }
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _port.BaseStream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _port.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_port.ReadTimeout);
            try
            {
                return await _port.BaseStream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-read window elapsed with 0 bytes; keep the COM handle open so handshake loops can retry immediately!
                return 0;
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public void Dispose()
        {
            try { if (_port.IsOpen) _port.Close(); } catch { }
            _port.Dispose();
        }
    }
}
