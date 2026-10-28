using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Logging;
using TranssionLab.Protocols.MediaTek;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Unisoc;
using TranssionLab.Protocols.Usb;
using TranssionLab.Tests.Mocks;
using TranssionLab.ViewModels;
using TranssionLab.ViewModels.Pages;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class VcomCatchAndQuickReadTests : IDisposable
{
    private readonly string _tempDir;

    public VcomCatchAndQuickReadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tl-vcom-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock races
        }
    }

    [Fact]
    public async Task MediaTekProvider_DrainsPreloaderReadyBannerAndDuplicate5F_NormalizesCom35_AndHoldsHandshake()
    {
        int a0Count = 0;
        var serial = new SimulatedSerialChannelFactory((port, tx) =>
        {
            Assert.Equal("COM35", port);
            return tx[0] switch
            {
                0xA0 => (++a0Count == 1)
                    ? "READY\r\n"u8.ToArray()
                    : [0x5F, 0x5F],
                0x0A => [0xF5],
                0x50 => [0xAF],
                0x05 => [0xFA],
                0xFD => [0xFD, 0x09, 0x50, 0x00, 0x00],
                0xFC => [0xFC, 0x8A, 0x00, 0xCA, 0x00, 0x00, 0x01, 0x00, 0x00],
                0xD8 => [0xD8, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00],
                0xFE => [0x03],
                0xE1 => [0xE1, 0x00, 0x00, 0x00, 0x08, 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44, 0x00, 0x00],
                _ => []
            };
        });

        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs"));
        var mtk = new MediaTekProvider(serial, logger);

        var res = await mtk.ProbeReadOnlyBromOrPreloaderAsync("MediaTek PreLoader USB VCOM (Android) (COM35)");

        Assert.True(res.PortOpened);
        Assert.True(res.SyncSucceeded);
        Assert.Equal((ushort)0x0950, res.HardwareCode);
        Assert.Contains("MT6835", res.ChipsetFamilySummary);
        Assert.Contains("Preloader (v3)", res.DiagnosticMessage);
        Assert.Equal("AABBCCDD11223344", res.MeIdHex);
        Assert.True(res.SecureBootEnabled);
        Assert.False(res.SerialLinkAuthRequired);
        Assert.True(res.DownloadAgentAuthRequired);
    }

    [Fact]
    public async Task MediaTekProvider_RecoversWhenBufferedA0ResetsRealPreloaderStartCmdStateMachine()
    {
        // Exact C state machine inside real MediaTek Preloader sec_usbdl.c:
        // Always echoes ~c, and if extra 0xA0 arrives when syncIndex==1, resets syncIndex=0!
        byte[] pass = [0xA0, 0x0A, 0x50, 0x05];
        int syncIndex = 0;
        bool inCommandMode = false;
        bool injectedDoubleA0 = false;

        var serial = new SimulatedSerialChannelFactory((_, tx) =>
        {
            byte c = tx[0];
            if (!inCommandMode)
            {
                // Simulate two 0xA0 bytes hitting Preloader on the very first write (setting syncIndex=1 then resetting syncIndex=0!)
                if (!injectedDoubleA0 && c == 0xA0)
                {
                    injectedDoubleA0 = true;
                    syncIndex = 0; // 1st 0xA0 -> 1, 2nd 0xA0 -> reset to 0!
                    return [0x5F, 0x5F];
                }

                byte echo = (byte)~c;
                if (c == pass[syncIndex])
                {
                    syncIndex++;
                    if (syncIndex == 4)
                    {
                        inCommandMode = true;
                    }
                }
                else
                {
                    syncIndex = 0;
                }
                return [echo];
            }

            return c switch
            {
                0xFD => [0xFD, 0x07, 0x07, 0x00, 0x00], // MT6768 Helio G85/G88
                0xFC => [0xFC, 0x8A, 0x00, 0xCA, 0x00, 0x00, 0x01, 0x00, 0x00],
                0xD8 => [0xD8, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00],
                0xFE => [0x04],
                _ => []
            };
        });

        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs_resync"));
        var mtk = new MediaTekProvider(serial, logger);

        var res = await mtk.ProbeReadOnlyBromOrPreloaderAsync("COM22");

        Assert.True(res.SyncSucceeded);
        Assert.Equal((ushort)0x0707, res.HardwareCode);
        Assert.Contains("MT6768", res.ChipsetFamilySummary);
    }

    [Fact]
    public async Task QuickRead_CatchesSprdEnumUnisocBootRomWithIpChecksum_WithoutBlockingOnSlowAdbServer()
    {
        var pnp = new SimulatedPnpBackend();

        // Build real Spreadtrum BootROM BSL_REP_VER (0x0081) frame using Spreadtrum BootROM IP checksum (as used by T606/SC9863A!)
        var verBytes = Encoding.ASCII.GetBytes("Spreadtrum Boot Block version 1.2");
        var rawHeaderAndBody = new byte[4 + verBytes.Length];
        rawHeaderAndBody[0] = 0x00;
        rawHeaderAndBody[1] = 0x81;
        rawHeaderAndBody[2] = 0x00;
        rawHeaderAndBody[3] = (byte)verBytes.Length;
        verBytes.CopyTo(rawHeaderAndBody, 4);
        ushort bootRomChk = SpdUnisocProvider.ComputeSprdBootRomChecksum(rawHeaderAndBody, littleEndianWords: false);

        var frame = new List<byte> { 0x7E };
        frame.AddRange(rawHeaderAndBody);
        frame.Add((byte)(bootRomChk >> 8));
        frame.Add((byte)(bootRomChk & 0xFF));
        frame.Add(0x7E);

        var serial = new SimulatedSerialChannelFactory((port, tx) =>
        {
            Assert.Equal("COM14", port);
            return tx[0] == 0x7E ? frame.ToArray() : [];
        });

        var proc = new RecordedProcessRunner((_, _) =>
        {
            // Simulate slow adb daemon startup; dedicated VCOM loop must NOT be blocked by this!
            Thread.Sleep(250);
            return new CommandExecutionResult(true, 0, "List of devices attached\n", "", 250);
        });

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "sprd_enum"),
            customPnpBackend: pnp,
            customProcessRunner: proc,
            customSerialFactory: serial);

        using var sp = services.BuildServiceProvider();
        var quickRead = sp.GetRequiredService<IPhoneQuickReadService>();

        pnp.Mutate(b =>
        {
            // Real Spreadtrum SciU2S bus-enumerated DeviceID WITHOUT USB\VID_xxxx!
            b.Devices.Add(new RawPnpDeviceRecord(
                @"SPRD_ENUM\SCI_DIAG\5&1982CC1&0&0",
                "SPRD U2S Diag (COM14)",
                "Spreadtrum",
                "SciU2S",
                0,
                "OK"));
            b.ComPorts.Add("COM14");
        });

        var report = await quickRead.CatchAndReadPhoneAsync(
            QuickReadTransportFilter.AutoDetectAll,
            "ITEL",
            TimeSpan.FromSeconds(5));

        Assert.True(report.Succeeded);
        Assert.Equal("COM14", report.PortOrSerial);
        Assert.Contains("Spreadtrum Boot Block version 1.2", report.ChipsetSummary);
        Assert.Equal(DataProvenance.Unknown, report.ModelProvenance);
    }

    [Fact]
    public void WinUsbBracketToken_AndSevenSectionNavigation_WorkCleanly()
    {
        const string winUsbFriendly = @"MT65xx Preloader [WINUSB:\\?\usb#vid_0e8d&pid_2000#6&11aa22#a5dcbf10]";
        var extracted = UsbSignatureCatalog.ExtractComPortFromName(winUsbFriendly);
        Assert.Equal(@"WINUSB:\\?\usb#vid_0e8d&pid_2000#6&11aa22#a5dcbf10", extracted);
        Assert.Equal(extracted, PlatformToolLocator.NormalizeSerialPortName(extracted!));

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "nav_test"),
            customPnpBackend: new SimulatedPnpBackend(),
            customProcessRunner: new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "", "", 1)),
            customSerialFactory: new SimulatedSerialChannelFactory((_, _) => []));

        using var sp = services.BuildServiceProvider();
        var shell = sp.GetRequiredService<MainShellViewModel>();

        Assert.Equal(11, shell.PrimaryNavItems.Count);
        Assert.Equal(19, shell.Pages.Count);
        Assert.Contains("LIFETIME UNLIMITED", shell.UnlimitedStatusBadge);

        // Selecting Module 10 (Transsion Rom Studio) exposes RomStudio + FirmwareExplorer sub-tabs
        shell.SelectedPrimaryNav = shell.PrimaryNavItems[9];
        Assert.True(shell.HasMultipleSubTabs);
        Assert.Equal(2, shell.ActiveSubTabs.Count);
        Assert.Equal(12, shell.CurrentPage.PageNumber);

        // Settings gear wheel opens Settings (#18) + USB/Profiles/Logs sub-tabs
        shell.OpenSettingsWheelCommand.Execute(null);
        Assert.Equal(18, shell.CurrentPage.PageNumber);
        Assert.Equal(6, shell.ActiveSubTabs.Count);

        // Emergency STOP resets badge cleanly
        shell.EmergencyStopCommand.Execute(null);
        Assert.Equal("STOPPED", shell.StepBadgeText);

        // Fastboot OGT Flasher folder import skips protected NVRAM/FRP/userdata images automatically
        var importDir = Path.Combine(_tempDir, "fw_folder");
        Directory.CreateDirectory(importDir);
        File.WriteAllBytes(Path.Combine(importDir, "boot_a.img"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(importDir, "vendor_boot_a.img"), [5, 6, 7, 8]);
        File.WriteAllBytes(Path.Combine(importDir, "proinfo.img"), [9, 9]);
        File.WriteAllBytes(Path.Combine(importDir, "nvram.img"), [9, 9]);
        File.WriteAllBytes(Path.Combine(importDir, "userdata.img"), [9, 9]);

        var flashVm = sp.GetRequiredService<TranssionLab.ViewModels.Pages.FlashManagerViewModel>();
        int imported = flashVm.ImportUnprotectedImagesFromDirectory(importDir);
        Assert.Equal(2, imported);
        Assert.Equal(2, flashVm.FlashQueue.Count);
        flashVm.UnselectAllQueueCommand.Execute(null);
        Assert.False(flashVm.BuildPlanCommand.CanExecute(null));
        flashVm.SelectAllQueueCommand.Execute(null);
        Assert.True(flashVm.BuildPlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task GlobalReadPhoneInfoCommand_CatchesTransientVcomPort_UpdatesConsoleAndStepTracker_WithoutInferringModel()
    {
        var pnp = new SimulatedPnpBackend();
        var proc = new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "List of devices attached\n", "", 2));
        var serial = new SimulatedSerialChannelFactory((_, tx) => tx[0] switch
        {
            0xA0 => [0x5F],
            0x0A => [0xF5],
            0x50 => [0xAF],
            0x05 => [0xFA],
            0xFD => [0xFD, 0x07, 0x88, 0x00, 0x00],
            0xFC => [0xFC, 0x8A, 0x00, 0xCA, 0x00, 0x00, 0x01, 0x00, 0x00],
            0xD8 => [0xD8, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00],
            0xFE => [0x04],
            _ => []
        });

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "shell_vcom"),
            customPnpBackend: pnp,
            customProcessRunner: proc,
            customSerialFactory: serial);

        using var sp = services.BuildServiceProvider();
        var shell = sp.GetRequiredService<MainShellViewModel>();
        var home = sp.GetRequiredService<DashboardViewModel>();

        var readTask = shell.GlobalReadPhoneInfoCommand.ExecuteAsync();

        await Task.Delay(50);
        pnp.Mutate(b =>
        {
            b.Devices.Add(new RawPnpDeviceRecord(
                @"USB\VID_0E8D&PID_2000\6&2A88B11&0&3",
                "USB Serial Device (COM18)",
                "Microsoft",
                "usbser",
                0,
                "OK"));
            b.ComPorts.Add("COM18");
        });

        await readTask;

        Assert.NotNull(home.LastQuickReadReport);
        Assert.True(home.LastQuickReadReport!.Succeeded);
        Assert.Equal("COM18", home.LastQuickReadReport.PortOrSerial);
        Assert.Contains("MT6789", home.LastQuickReadReport.ChipsetSummary);
        Assert.Equal(DataProvenance.Unknown, home.LastQuickReadReport.ModelProvenance);
        Assert.Equal("DONE 4/4", shell.StepBadgeText);
        Assert.Contains("COM18", shell.LiveConsoleText);
    }

    [Fact]
    public async Task QuickReadService_WaitsThroughUnauthorizedAdb_ThenReadsVerifiedTecnoProfileOnceAuthorized()
    {
        int listCalls = 0;
        var pnp = new SimulatedPnpBackend();
        var proc = new RecordedProcessRunner((exe, args) =>
        {
            if (args.Contains("--version") || args.Contains("version"))
            {
                return new CommandExecutionResult(true, 0, "Android Debug Bridge version 1.0.41", "", 1);
            }

            if (args.Contains("devices"))
            {
                if (exe.Contains("fastboot", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandExecutionResult(true, 0, "", "", 1);
                }

                listCalls++;
                return listCalls == 1
                    ? new CommandExecutionResult(true, 0, "List of devices attached\n0899123456\tunauthorized usb:1-2\n", "", 2)
                    : new CommandExecutionResult(true, 0, "List of devices attached\n0899123456\tdevice product:CL6-GL model:TECNO_CL6 device:TECNO-CL6 transport_id:2\n", "", 2);
            }

            if (args.Contains("getprop"))
            {
                const string props = """
                    [ro.product.manufacturer]: [TECNO]
                    [ro.product.brand]: [TECNO]
                    [ro.product.model]: [TECNO CL6]
                    [ro.tran.market.name]: [TECNO CAMON 30]
                    [ro.product.device]: [TECNO-CL6]
                    [ro.board.platform]: [mt6789]
                    [ro.build.version.release]: [14]
                    [ro.build.version.security_patch]: [2026-06-05]
                    [ro.build.display.id]: [CL6-H894ABCD-U-260605V120]
                    [ro.boot.slot_suffix]: [_a]
                    [ro.boot.flash.locked]: [1]
                    """;
                return new CommandExecutionResult(true, 0, props, "", 5);
            }

            return new CommandExecutionResult(true, 0, "level: 84", "", 2);
        });

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "adb_auth"),
            customPnpBackend: pnp,
            customProcessRunner: proc,
            customSerialFactory: new SimulatedSerialChannelFactory((_, _) => []));

        using var sp = services.BuildServiceProvider();
        var quickRead = sp.GetRequiredService<IPhoneQuickReadService>();
        var consoleLines = new List<string>();

        var report = await quickRead.CatchAndReadPhoneAsync(
            QuickReadTransportFilter.AdbAndFastboot,
            "TECNO",
            TimeSpan.FromSeconds(10),
            null,
            line => consoleLines.Add(line));

        Assert.True(report.Succeeded);
        Assert.Equal("0899123456", report.PortOrSerial);
        Assert.Contains("CL6", report.ModelSummary);
        Assert.Contains(consoleLines, l => l.Contains("Allow USB Debugging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MetaMode_LogsPlugDeviceInstructions_WaitingForUsbDevice_DetectsVcom_AndHandshakesDeviceToMetaMode()
    {
        var pnp = new SimulatedPnpBackend();
        var proc = new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "", "", 1));
        var serial = new SimulatedSerialChannelFactory((_, tx) =>
        {
            var ascii = System.Text.Encoding.ASCII.GetString(tx);
            if (ascii.Contains("ADVEMETA", StringComparison.Ordinal))
            {
                return "READYATEMEVDA"u8.ToArray();
            }
            if (ascii.Contains("METAMETA", StringComparison.Ordinal))
            {
                return "READYATEMATEM"u8.ToArray();
            }
            return [];
        });

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "meta_flow"),
            customPnpBackend: pnp,
            customProcessRunner: proc,
            customSerialFactory: serial);

        using var sp = services.BuildServiceProvider();
        var shell = sp.GetRequiredService<MainShellViewModel>();
        var metaVm = sp.GetRequiredService<MetaModeViewModel>();
        shell.CurrentPage = metaVm;

        var bootTask = metaVm.BootPhoneToMetaModeCommand.ExecuteAsync();

        var waitDeadline = DateTime.UtcNow.AddSeconds(2);
        while (!shell.LiveConsoleText.Contains("Waiting for USB device...", StringComparison.Ordinal) &&
               DateTime.UtcNow < waitDeadline)
        {
            await Task.Delay(10);
        }

        // Before plugging USB, console MUST already show instructions + "Waiting for USB device..."
        Assert.Contains("Instructions:", shell.LiveConsoleText);
        Assert.Contains("Plug the device", shell.LiveConsoleText);
        Assert.Contains("Waiting for USB device...", shell.LiveConsoleText);

        // Now user plugs in powered-OFF MediaTek phone -> Preloader VCOM (COM35) appears
        pnp.Mutate(b =>
        {
            b.Devices.Add(new RawPnpDeviceRecord(
                @"USB\VID_0E8D&PID_2000\6&998877&0&1",
                "MediaTek PreLoader USB VCOM (COM35)",
                "MediaTek Inc.",
                "usbser",
                0,
                "OK"));
            b.ComPorts.Add("COM35");
        });

        await bootTask;

        var log = shell.LiveConsoleText;
        int idxInstructions = log.IndexOf("Instructions:", StringComparison.Ordinal);
        Assert.True(idxInstructions >= 0, $"Expected Instructions in log:\n{log}");

        int idxWaiting = log.IndexOf("Waiting for USB device...", idxInstructions, StringComparison.Ordinal);
        Assert.True(idxWaiting > idxInstructions, $"Expected Waiting for USB device after Instructions:\n{log}");

        int idxDetected = log.IndexOf("USB VCOM Detected: MediaTek PreLoader USB VCOM (COM35) [VID_0E8D&PID_2000]", idxWaiting, StringComparison.Ordinal);
        Assert.True(idxDetected > idxWaiting, $"Expected VCOM Detected after Waiting:\n{log}");

        int idxHandshake = log.IndexOf("Handshaking device on COM35", idxDetected, StringComparison.Ordinal);
        Assert.True(idxHandshake > idxDetected, $"Expected Handshaking device after VCOM Detected:\n{log}");

        int idxAck = log.IndexOf("ADVEMETA -> ATEMEVDA", idxHandshake, StringComparison.Ordinal);
        Assert.True(idxAck > idxHandshake, $"Expected ATEMEVDA ACK after Handshaking:\n{log}");
        Assert.NotNull(metaVm.LastDiagnostic);
        Assert.True(metaVm.LastDiagnostic!.UsbEndpointVerified);
    }

    [Fact]
    public async Task MtkAndSpdTools_LogInstructions_WaitingForUsbDevice_DetectVcom_AndHandshakeDevice()
    {
        var pnp = new SimulatedPnpBackend();
        var proc = new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "", "", 1));
        var spdProvider = new TranssionLab.Protocols.Unisoc.SpdUnisocProvider(
            new SimulatedSerialChannelFactory((_, _) => []),
            new TranssionLab.Logging.StructuredLogger(Path.Combine(_tempDir, "spd_log"), () => true));
        byte[] spdVerReply = spdProvider.EncodeHdlcFrame(0x0081, "Spreadtrum Boot Block version 1.2"u8);

        var serial = new SimulatedSerialChannelFactory((port, tx) =>
        {
            if (port == "COM14" && tx.Length > 0 && tx[0] == 0x7E)
            {
                return spdVerReply;
            }
            return tx.Length > 0 ? tx[0] switch
            {
                0xA0 => [0x5F],
                0x0A => [0xF5],
                0x50 => [0xAF],
                0x05 => [0xFA],
                0xFD => [0xFD, 0x07, 0x07, 0x00, 0x00],
                0xFC => [0xFC, 0x8A, 0x00, 0xCA, 0x00, 0x00, 0x00, 0x00, 0x00],
                0xD8 => [0xD8, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00],
                0xFE => [0x04],
                _ => []
            } : [];
        });

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "mtk_spd_flow"),
            customPnpBackend: pnp,
            customProcessRunner: proc,
            customSerialFactory: serial);

        using var sp = services.BuildServiceProvider();
        var shell = sp.GetRequiredService<MainShellViewModel>();
        var spdVm = sp.GetRequiredService<SpdUnisocToolsViewModel>();
        shell.CurrentPage = spdVm;

        var spdTask = spdVm.ProbeDownloadBannerCommand.ExecuteAsync();
        await Task.Delay(30);

        pnp.Mutate(b =>
        {
            b.Devices.Add(new RawPnpDeviceRecord(
                @"USB\VID_1782&PID_4D00\6&556677&0&2",
                "SPRD U2S Diag (COM14)",
                "Spreadtrum",
                "SciU2S",
                0,
                "OK"));
            b.ComPorts.Add("COM14");
        });

        await spdTask;

        Assert.Contains("Instructions:", shell.LiveConsoleText);
        Assert.Contains("Waiting for USB device...", shell.LiveConsoleText);
        Assert.Contains("USB VCOM Detected: SPRD U2S Diag (COM14) [VID_1782&PID_4D00]", shell.LiveConsoleText);
        Assert.Contains("Handshaking device on COM14", shell.LiveConsoleText);
        Assert.NotNull(spdVm.LastProbeResult);
        Assert.True(spdVm.LastProbeResult!.CheckBaudAckReceived);
    }

    [Fact]
    public async Task AdbAndFastbootBenchTools_InstallApk_ManageApps_BlockProtectedMdm_Screenshot_Wireless_TempBoot_AndDriverAudit()
    {
        var apkPath = Path.Combine(_tempDir, "bench_helper.apk");
        await File.WriteAllBytesAsync(apkPath, [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02]);

        var bootImgPath = Path.Combine(_tempDir, "test_boot.img");
        var bootBytes = new byte[8192];
        "ANDROID!"u8.CopyTo(bootBytes);
        await File.WriteAllBytesAsync(bootImgPath, bootBytes);

        var badImgPath = Path.Combine(_tempDir, "not_a_boot.img");
        await File.WriteAllBytesAsync(badImgPath, [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]);

        var proc = new RecordedProcessRunner((exe, args) =>
        {
            if (exe.Contains("pnputil", StringComparison.OrdinalIgnoreCase))
            {
                return new CommandExecutionResult(true, 0, "Original Name : usb2ser.inf\nProvider Name : MediaTek Inc.\nOriginal Name : android_winusb.inf\n", "", 3);
            }
            if (args.Contains("install"))
            {
                return new CommandExecutionResult(true, 0, "Performing Streamed Install\nSuccess\n", "", 5);
            }
            if (args.Contains("packages"))
            {
                return new CommandExecutionResult(true, 0, "package:com.example.benchapp\npackage:org.demo.notes\n", "", 2);
            }
            if (args.Contains("uninstall"))
            {
                return new CommandExecutionResult(true, 0, "Success\n", "", 2);
            }
            if (args.Contains("pull"))
            {
                var dst = args[^1];
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
                return new CommandExecutionResult(true, 0, "1 file pulled", "", 2);
            }
            if (args.Contains("logcat"))
            {
                return new CommandExecutionResult(true, 0, "10-28 12:00:00.000  1000  1000 I ActivityManager: Boot completed\n", "", 2);
            }
            if (args.Contains("pair"))
            {
                return new CommandExecutionResult(true, 0, "Successfully paired to 192.168.1.40:39123 [guid=adb-TECNO]\n", "", 3);
            }
            if (args.Contains("connect"))
            {
                return new CommandExecutionResult(true, 0, "connected to 192.168.1.40:41555\n", "", 3);
            }
            return new CommandExecutionResult(true, 0, "OKAY", "OKAY", 2);
        });

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "bench_tools"),
            customPnpBackend: new SimulatedPnpBackend(),
            customProcessRunner: proc,
            customSerialFactory: new SimulatedSerialChannelFactory((_, _) => []));

        using var sp = services.BuildServiceProvider();
        var adb = sp.GetRequiredService<IAdbProvider>();
        var fastboot = sp.GetRequiredService<IFastbootProvider>();
        var connVm = sp.GetRequiredService<ConnectionManagerViewModel>();

        // 1. APK Install
        var installRes = await adb.InstallApkAsync("SER123", apkPath);
        Assert.True(installRes.Succeeded);

        // 2. Package list + safe user uninstall + strict anti-MDM/core OS guardrail
        var pkgs = await adb.ListPackagesAsync("SER123", thirdPartyOnly: true);
        Assert.Equal(2, pkgs.Count);
        var uninst = await adb.UninstallUserPackageAsync("SER123", "com.example.benchapp");
        Assert.True(uninst.Succeeded);

        await Assert.ThrowsAsync<TranssionLab.Core.Exceptions.SafetyPolicyViolationException>(
            () => adb.UninstallUserPackageAsync("SER123", "com.android.phone"));
        await Assert.ThrowsAsync<TranssionLab.Core.Exceptions.SafetyPolicyViolationException>(
            () => adb.UninstallUserPackageAsync("SER123", "com.enterprise.mdm.lock"));

        // 3. Screenshot PNG + Logcat + Wireless Pair & Connect
        var shotOut = Path.Combine(_tempDir, "cap.png");
        var savedShot = await adb.CaptureScreenshotAsync("SER123", shotOut);
        Assert.True(File.Exists(savedShot));

        var logcat = await adb.CaptureLogcatSnapshotAsync("SER123", 200);
        Assert.Contains("Boot completed", logcat);

        Assert.True((await adb.PairWirelessAdbAsync("192.168.1.40:39123", "654321")).Succeeded);
        Assert.True((await adb.ConnectWirelessAdbAsync("192.168.1.40:41555")).Succeeded);

        // 4. Fastboot temporary boot magic check & userdata wipe
        await Assert.ThrowsAsync<TranssionLab.Core.Exceptions.ServicingException>(
            () => fastboot.BootTemporaryImageAsync("FB123", badImgPath));
        Assert.True((await fastboot.BootTemporaryImageAsync("FB123", bootImgPath)).Succeeded);
        Assert.True((await fastboot.WipeUserdataAndMetadataAsync("FB123")).Succeeded);

        // 5. Read-only signed Windows Driver Store audit
        await connVm.AuditInstalledDriversCommand.ExecuteAsync();
        Assert.Contains("MediaTek USB VCOM (usb2ser / MediaTek)  : INSTALLED [OK]", connVm.DriverAuditReportText);
    }

    [Fact]
    public async Task FactoryBoot_ReadOnlyAtModem_SuperLogicalUnpack_AndBootImgUnpack_WorkEndToEnd()
    {
        var serial = new SimulatedSerialChannelFactory((_, tx) =>
        {
            var s = System.Text.Encoding.ASCII.GetString(tx);
            if (s.Contains("FACTFACT", StringComparison.Ordinal)) return "READYTCAFTCAF"u8.ToArray();
            if (s.StartsWith("AT+CGMI", StringComparison.Ordinal)) return "TECNO MOBILE LIMITED\r\nOK\r\n"u8.ToArray();
            if (s.StartsWith("AT+CGMM", StringComparison.Ordinal)) return "TECNO CL6\r\nOK\r\n"u8.ToArray();
            if (s.StartsWith("AT+CGMR", StringComparison.Ordinal)) return "MOLY.LR13.R2.MP.V98.P12\r\nOK\r\n"u8.ToArray();
            if (s.StartsWith("AT", StringComparison.Ordinal)) return "MT6789 Modem\r\nOK\r\n"u8.ToArray();
            return [];
        });

        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "unpack_at"),
            customPnpBackend: new SimulatedPnpBackend(),
            customProcessRunner: new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "", "", 1)),
            customSerialFactory: serial);

        using var sp = services.BuildServiceProvider();
        var mtk = sp.GetRequiredService<IMediaTekProvider>();
        var meta = sp.GetRequiredService<IMetaModeProvider>();
        var fw = sp.GetRequiredService<IFirmwareFormatRegistry>();

        // 1. Preloader Factory Mode handshake (FACTFACT -> TCAFTCAF)
        var factRes = await mtk.RequestPreloaderBootModeAsync("COM21", PreloaderBootRequestKind.FactoryMode);
        Assert.True(factRes.AckReceived);
        Assert.Equal("TCAFTCAF", factRes.ReceivedAckToken);

        // 2. Read-only 3GPP AT Modem / Baseband inquiry
        var atRes = await meta.QueryReadOnlyModemAtInfoAsync("COM38");
        Assert.True(atRes.AtAckReceived);
        Assert.Equal("TECNO MOBILE LIMITED", atRes.Manufacturer);
        Assert.Equal("TECNO CL6", atRes.Model);
        Assert.Contains("MOLY.LR13", atRes.FirmwareRevision);

        // 3. Synthesize a valid raw super.img with LpMetadata + linear extent for 'product_a' and extract it!
        var (superBytes, expectedPayload, v4Bytes) = BuildSyntheticSuperAndBootPayloads();
        var superPath = Path.Combine(_tempDir, "super_test.img");
        await File.WriteAllBytesAsync(superPath, superBytes);

        var extractedProduct = await fw.ExtractArchiveEntryAsync(superPath, "product_a", Path.Combine(_tempDir, "super_out"));
        Assert.Equal(expectedPayload, await File.ReadAllBytesAsync(extractedProduct));

        // 4. Synthesize an ANDROID! v4 boot.img and unpack kernel + ramdisk + JSON metadata!
        var v4BootPath = Path.Combine(_tempDir, "boot_v4.img");
        await File.WriteAllBytesAsync(v4BootPath, v4Bytes);

        var unpackReport = await fw.UnpackBootOrVendorBootImageAsync(v4BootPath, Path.Combine(_tempDir, "boot_unpacked"));
        Assert.NotNull(unpackReport.KernelPath);
        Assert.NotNull(unpackReport.RamdiskPath);
        Assert.Equal(512, new FileInfo(unpackReport.KernelPath!).Length);
        Assert.Equal(256, new FileInfo(unpackReport.RamdiskPath!).Length);
        Assert.True(File.Exists(unpackReport.MetadataJsonPath));
    }

    private static (byte[] SuperBytes, byte[] ExpectedPayload, byte[] V4BootBytes) BuildSyntheticSuperAndBootPayloads()
    {
        var superBytes = new byte[0x8000]; // 32 KiB
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(superBytes.AsSpan(0x1000, 4), 0x616C4467); // Geometry magic
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(superBytes.AsSpan(0x1000 + 40, 4), 65536);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(superBytes.AsSpan(0x1000 + 44, 4), 2);

        int hdrOff = 0x3000;
        var hdr = superBytes.AsSpan(hdrOff);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr[..4], 0x414C5030); // LP header magic
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(4, 2), 10);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(6, 2), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8, 4), 128); // header_size = 128

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(76, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(80, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(84, 4), 52);

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(88, 4), 52);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(92, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(96, 4), 24);

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(100, 4), 76);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(104, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(108, 4), 48);

        int tablesBase = hdrOff + 128;
        "product_a"u8.CopyTo(superBytes.AsSpan(tablesBase, 36));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(superBytes.AsSpan(tablesBase + 40, 4), 0); // first_extent = 0
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(superBytes.AsSpan(tablesBase + 44, 4), 1); // num_extents = 1

        int extPos = tablesBase + 52;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(superBytes.AsSpan(extPos, 8), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(superBytes.AsSpan(extPos + 8, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(superBytes.AsSpan(extPos + 12, 8), 48);
        "main_a"u8.CopyTo(superBytes.AsSpan(tablesBase + 76, 36));

        var expectedPayload = new byte[1024];
        for (int i = 0; i < expectedPayload.Length; i++) expectedPayload[i] = (byte)((i * 7 + 13) & 0xFF);
        expectedPayload.CopyTo(superBytes.AsSpan(0x6000, 1024));

        var v4Bytes = new byte[12288]; // 3 pages (header + 1 page kernel + 1 page ramdisk)
        "ANDROID!"u8.CopyTo(v4Bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v4Bytes.AsSpan(8, 4), 512);  // kernel_size = 512
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v4Bytes.AsSpan(12, 4), 256); // ramdisk_size = 256
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v4Bytes.AsSpan(20, 4), 1580);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v4Bytes.AsSpan(40, 4), 4);   // header_version = 4
        "console=tty0 androidboot.hardware=mt6789"u8.CopyTo(v4Bytes.AsSpan(44));
        "KERNEL_PAYLOAD_MAGIC"u8.CopyTo(v4Bytes.AsSpan(4096));
        "RAMDISK_CPIO_MAGIC"u8.CopyTo(v4Bytes.AsSpan(8192));

        return (superBytes, expectedPayload, v4Bytes);
    }
}
