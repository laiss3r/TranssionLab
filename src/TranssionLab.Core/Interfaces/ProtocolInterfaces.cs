using TranssionLab.Core.Enums;
using TranssionLab.Core.Models;

namespace TranssionLab.Core.Interfaces;

// --- Section 4 Required Connection & Protocol Interfaces ---

public interface IUsbInterfaceProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }
    event EventHandler<UsbConnectionHistoryEvent>? UsbDeviceEvent;

    Task<IReadOnlyList<DetectedUsbDevice>> EnumerateUsbDevicesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DetectedUsbDevice>> EnumerateFastVcomDevicesAsync(CancellationToken ct = default) =>
        EnumerateUsbDevicesAsync(ct);
    Task<IReadOnlyList<ComPortDescriptor>> EnumerateComPortsAsync(CancellationToken ct = default);
    IReadOnlyList<UsbConnectionHistoryEvent> GetConnectionHistory();
    void StartMonitoring();
    void StopMonitoring();
}

public interface IDeviceDiscoveryProvider
{
    event EventHandler? InventoryChanged;
    event EventHandler<string?>? WaitingStatusChanged;

    IReadOnlyList<ConnectedDeviceSnapshot> ConnectedDevices { get; }
    ConnectedDeviceSnapshot? SelectedDevice { get; }
    bool RequiresExplicitSelection { get; }
    bool IsWaitingForDevice { get; }
    string? WaitingStatusMessage { get; }

    Task<IReadOnlyList<ConnectedDeviceSnapshot>> RefreshInventoryAsync(CancellationToken ct = default);
    void SelectDeviceExplicitly(string? deviceKey);
    ConnectedDeviceSnapshot RequireExplicitlySelectedDevice();
    void CancelActiveWait();

    Task<ConnectedDeviceSnapshot> WaitForDeviceAsync(
        string activityName,
        IReadOnlyCollection<ConnectionMode>? acceptedModes = null,
        TimeSpan? timeout = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default);

    Task<DetectedUsbDevice> WaitForVendorUsbEndpointAsync(
        string activityName,
        Func<DetectedUsbDevice, bool> predicate,
        string connectionHint,
        TimeSpan? timeout = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}

public interface IAdbProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }

    Task<bool> IsToolAvailableAsync(CancellationToken ct = default);
    Task<string?> GetToolVersionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdbDiscoveredEndpoint>> ListDevicesAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetDevicePropertiesAsync(string serial, CancellationToken ct = default);
    Task<string> CaptureBatterySummaryAsync(string serial, CancellationToken ct = default);
    Task<string> CaptureStorageSummaryAsync(string serial, CancellationToken ct = default);
    Task<IReadOnlyList<AdbBlockPartitionEntry>> EnumerateBlockPartitionsAsync(string serial, CancellationToken ct = default);
    Task<(CommandExecutionResult Result, long BytesRead, string? Sha256Hex)> ReadbackPartitionToFileAsync(
        string serial,
        string blockNodePath,
        string localDestinationPath,
        CancellationToken ct = default);
    Task<CommandExecutionResult> RebootAsync(string serial, string targetMode, CancellationToken ct = default);
    Task<CommandExecutionResult> SideloadPackageAsync(string serial, string otaZipPath, CancellationToken ct = default);

    Task<CommandExecutionResult> InstallApkAsync(string serial, string apkFilePath, CancellationToken ct = default) =>
        Task.FromResult(new CommandExecutionResult(false, -1, "", "Not implemented by mock.", 0));

    Task<IReadOnlyList<string>> ListPackagesAsync(string serial, bool thirdPartyOnly = true, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    Task<CommandExecutionResult> UninstallUserPackageAsync(string serial, string packageName, CancellationToken ct = default) =>
        Task.FromResult(new CommandExecutionResult(false, -1, "", "Not implemented by mock.", 0));

    Task<string> CaptureScreenshotAsync(string serial, string outputPngPath, CancellationToken ct = default) =>
        Task.FromResult(string.Empty);

    Task<string> CaptureLogcatSnapshotAsync(string serial, int maxLines = 400, CancellationToken ct = default) =>
        Task.FromResult(string.Empty);

    Task<CommandExecutionResult> PairWirelessAdbAsync(string hostAndPort, string pairingCode, CancellationToken ct = default) =>
        Task.FromResult(new CommandExecutionResult(false, -1, "", "Not implemented by mock.", 0));

    Task<CommandExecutionResult> ConnectWirelessAdbAsync(string hostAndPort, CancellationToken ct = default) =>
        Task.FromResult(new CommandExecutionResult(false, -1, "", "Not implemented by mock.", 0));

    Task<CommandExecutionResult> RestartAdbServerAsync(CancellationToken ct = default) =>
        Task.FromResult(new CommandExecutionResult(false, -1, "", "Not implemented by mock.", 0));
}

public sealed record AdbDiscoveredEndpoint(
    string Serial,
    AdbDeviceState State,
    string? ProductHint,
    string? ModelHint,
    string? DeviceCodenameHint,
    string? TransportId);

public sealed record CommandExecutionResult(
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    long DurationMs);

public interface IRecoveryProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }

    Task<IReadOnlyList<AdbDiscoveredEndpoint>> DetectRecoveryAndSideloadEndpointsAsync(CancellationToken ct = default);
    Task<CommandExecutionResult> VerifyAndSideloadOtaAsync(string serial, string otaPackagePath, CancellationToken ct = default);
}

public interface IFastbootProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }

    Task<bool> IsToolAvailableAsync(CancellationToken ct = default);
    Task<string?> GetToolVersionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FastbootDiscoveredEndpoint>> ListDevicesAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetAllVariablesAsync(string serial, CancellationToken ct = default);
    Task<string?> GetVariableAsync(string serial, string variableName, CancellationToken ct = default);
    Task<bool?> QueryUnlockAbilityAsync(string serial, CancellationToken ct = default);
    Task<CommandExecutionResult> SetActiveSlotAsync(string serial, string slotLetter, CancellationToken ct = default);
    Task<CommandExecutionResult> RebootAsync(string serial, string targetMode, CancellationToken ct = default);
    Task<CommandExecutionResult> FlashPartitionAsync(string serial, string partitionName, string imagePath, CancellationToken ct = default);
    Task<CommandExecutionResult> FlashVbmetaAsync(
        string serial,
        string partitionName,
        string vbmetaImagePath,
        bool disableVerity,
        bool disableVerification,
        CancellationToken ct = default);
    Task<CommandExecutionResult> CreateLogicalPartitionAsync(string serial, string partitionName, long sizeBytes, CancellationToken ct = default);
    Task<CommandExecutionResult> ResizeLogicalPartitionAsync(string serial, string partitionName, long newSizeBytes, CancellationToken ct = default);
    Task<CommandExecutionResult> DeleteLogicalPartitionAsync(string serial, string partitionName, CancellationToken ct = default);
    Task<CommandExecutionResult> ErasePartitionAsync(string serial, string partitionName, CancellationToken ct = default);
    Task<CommandExecutionResult> ExecuteVerifiedUnlockCommandAsync(string serial, bool useLegacyOemCommand, CancellationToken ct = default);

    Task<CommandExecutionResult> BootTemporaryImageAsync(string serial, string bootImagePath, CancellationToken ct = default) =>
        Task.FromResult(new CommandExecutionResult(false, -1, "", "Not implemented by mock.", 0));

    Task<CommandExecutionResult> WipeUserdataAndMetadataAsync(string serial, CancellationToken ct = default) =>
        Task.FromResult(new CommandExecutionResult(false, -1, "", "Not implemented by mock.", 0));
}

public sealed record FastbootDiscoveredEndpoint(
    string Serial,
    ConnectionMode Mode, // FastbootBootloader vs FastbootdUserspace
    string? TransportHint);

public enum PreloaderBootRequestKind
{
    MetaModeUsb,
    MetaModeStandard,
    FastbootMode,
    FactoryMode
}

public sealed record PreloaderBootHandshakeResult(
    bool PortOpened,
    bool AckReceived,
    string SentToken,
    string? ReceivedAckToken,
    string DiagnosticMessage);

public interface IMediaTekProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }

    IReadOnlyList<DetectedUsbDevice> FilterMediaTekInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory);
    Task<MediaTekHandshakeProbeResult> ProbeReadOnlyBromOrPreloaderAsync(string comPort, CancellationToken ct = default);
    Task<PreloaderBootHandshakeResult> RequestPreloaderBootModeAsync(
        string comPort,
        PreloaderBootRequestKind mode = PreloaderBootRequestKind.MetaModeUsb,
        CancellationToken ct = default) =>
        Task.FromResult(new PreloaderBootHandshakeResult(false, false, "METAMETA", null, "Not implemented by mock provider."));
    string ResolveChipsetFamilyFromHwCode(ushort hwCode);
    string? HeldPortName => null;
    void ReleaseHeldPort() { }
}

public sealed record MediaTekHandshakeProbeResult(
    bool PortOpened,
    bool SyncSucceeded,
    ushort? HardwareCode,
    ushort? HwSubCode,
    ushort? HwVersion,
    ushort? SwVersion,
    bool SecureBootEnabled,
    bool SerialLinkAuthRequired,
    bool DownloadAgentAuthRequired,
    string ChipsetFamilySummary,
    ConnectionDashboardState ResultingState,
    string DiagnosticMessage,
    string? LoaderStageLabel = null,
    string? MeIdHex = null,
    bool PortHeldActive = false);

public interface IMetaModeProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }

    bool IsAuthorizedMetaLibraryInstalled(out string statusDetail);
    IReadOnlyList<MetaInterfaceDetection> DetectMetaInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory);
    Task<MetaSessionDiagnosticResult> DiagnoseMetaConnectionAsync(DetectedUsbDevice usbDevice, CancellationToken ct = default);
    Task<ReadOnlyModemAtInfoResult> QueryReadOnlyModemAtInfoAsync(string comPort, CancellationToken ct = default) =>
        Task.FromResult(new ReadOnlyModemAtInfoResult(false, false, null, null, null, "Not implemented by mock."));
}

public sealed record ReadOnlyModemAtInfoResult(
    bool PortOpened,
    bool AtAckReceived,
    string? Manufacturer,
    string? Model,
    string? FirmwareRevision,
    string RawSummary);

public sealed record MetaInterfaceDetection(
    DetectedUsbDevice UsbDevice,
    string InterfaceClassification,
    bool ExternalCommunicationProviderAvailable,
    ConnectionDashboardState ReportedState,
    IReadOnlyList<string> DocumentedCapabilities,
    IReadOnlyList<string> Prerequisites);

public sealed record MetaSessionDiagnosticResult(
    bool UsbEndpointVerified,
    bool CommunicationProviderAvailable,
    bool SessionEstablished,
    ConnectionDashboardState State,
    string DiagnosticSummary,
    IReadOnlyList<string> Limitations);

public interface ISpdUnisocProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }

    IReadOnlyList<DetectedUsbDevice> FilterUnisocInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory);
    Task<UnisocBootProbeResult> ProbeReadOnlyDownloadVersionAsync(string comPort, CancellationToken ct = default);
    byte[] EncodeHdlcFrame( ushort packetType, ReadOnlySpan<byte> payload);
    bool TryDecodeHdlcFrame(ReadOnlySpan<byte> frame, out ushort packetType, out byte[] payload);
    string? HeldPortName => null;
    void ReleaseHeldPort() { }
}

public sealed record UnisocBootProbeResult(
    bool PortOpened,
    bool CheckBaudAckReceived,
    string? BootRomBannerVersion,
    bool RequiresSignedFdl,
    ConnectionDashboardState ResultingState,
    string DiagnosticMessage,
    bool PortHeldActive = false);

public interface IDiagnosticProvider
{
    string ProviderName { get; }
    ProviderImplementationStatus ImplementationStatus { get; }

    IReadOnlyList<DiagInterfaceAssessment> ClassifyDiagnosticInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory);
    Task<DiagReadOnlyQueryResult> QueryReadOnlyDiagnosticVersionAsync(DiagInterfaceAssessment endpoint, CancellationToken ct = default);
}

public sealed record DiagInterfaceAssessment(
    DetectedUsbDevice UsbDevice,
    ChipsetVendor VendorFamily,
    string ProtocolFamily, // Qualcomm QCDM, Unisoc DIAG, MediaTek ELT
    bool SupportsReadOnlyVersionProbe,
    string GuardrailNotice);

public sealed record DiagReadOnlyQueryResult(
    bool Executed,
    bool Succeeded,
    string ProtocolFamily,
    string? ReportedCompileDate,
    string? ReportedFirmwareBuildId,
    string Summary);

public interface IDeviceProfileRepository
{
    IReadOnlyList<DeviceProfile> GetAllProfiles();
    DeviceProfile? GetById(string profileId);
    DeviceProfile? MatchVerifiedProfile(string? manufacturer, string? modelCode, string? codename, string? buildFingerprint);
    Task<DeviceProfile> ImportProfileFromFileAsync(string jsonFilePath, CancellationToken ct = default);
    Task<DeviceProfile> SaveOrPromoteProfileAsync(DeviceProfile profile, CancellationToken ct = default);
    IReadOnlyList<string> ValidateProfile(DeviceProfile profile);
}

public interface IProtocolCapabilityService
{
    IReadOnlyList<ProtocolCapabilitySummary> GetAllProviderSummaries();
    IReadOnlyList<OperationCapabilityEvaluation> EvaluateOperationsForDevice(ConnectedDeviceSnapshot? device);
}

public interface IOperationRegistry
{
    IReadOnlyList<OperationDescriptor> GetAllOperations();
    OperationDescriptor? GetById(string operationId);
}

public interface IStructuredLogger
{
    string CurrentSessionId { get; }
    event EventHandler<StructuredLogEntry>? EntryRecorded;

    void Log(StructuredLogEntry entry);
    void LogInfo(string provider, string action, string? output = null, string? deviceId = null, string? operationId = null);
    void LogWarning(string provider, string action, string message, string? deviceId = null, string? operationId = null);
    void LogError(string provider, string action, string errorMessage, int? exitCode = null, string? deviceId = null, string? operationId = null);

    IReadOnlyList<StructuredLogEntry> GetDisplayEntries(
        string? searchQuery = null,
        LogSeverity? minSeverity = null,
        string? providerFilter = null);

    IReadOnlyList<StructuredLogEntry> GetArchivedSessionEntries();
    void ClearDisplayOnly();
    Task ExportTxtAsync(string filePath, CancellationToken ct = default);
    Task ExportJsonAsync(string filePath, CancellationToken ct = default);
    string BuildTroubleshootingBundle(
        IReadOnlyList<ConnectedDeviceSnapshot> devices,
        IReadOnlyList<ProtocolCapabilitySummary> capabilities,
        IReadOnlyList<UsbConnectionHistoryEvent> usbHistory);
}

public interface IOperationHistoryStore
{
    IReadOnlyList<OperationHistoryRecord> GetRecords();
    Task RecordAsync(OperationHistoryRecord record, CancellationToken ct = default);
}

public sealed record BootImageUnpackReport(
    string SourceImagePath,
    string OutputDirectory,
    string HeaderMagic,
    uint HeaderVersion,
    uint PageSize,
    string? KernelPath,
    string? RamdiskPath,
    string? DtbPath,
    string MetadataJsonPath,
    string Summary);

public interface IFirmwareFormatRegistry
{
    IReadOnlyList<FirmwareFormatKind> SupportedFormats { get; }
    FirmwareFormatKind DetectFormat(string filePath);
    Task<FirmwareInspectionResult> InspectAsync(string filePath, CancellationToken ct = default);
    Task<string> ExtractArchiveEntryAsync(string packagePath, string entryName, string destinationDir, CancellationToken ct = default);
    Task<string> ConvertSparseImageToRawAsync(string sparseImagePath, string outputRawImagePath, CancellationToken ct = default);

    Task<string> ExtractSuperLogicalPartitionAsync(
        string superImagePath,
        string logicalPartitionName,
        string outputImagePath,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Not implemented by mock.");

    Task<BootImageUnpackReport> UnpackBootOrVendorBootImageAsync(
        string bootImagePath,
        string outputDirectory,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Not implemented by mock.");
}

public interface IPartitionManager
{
    Task<DevicePartitionTableSnapshot> ScanDevicePartitionsAsync(
        ConnectedDeviceSnapshot device,
        CancellationToken ct = default);

    Task<DevicePartitionTableSnapshot> LoadOfflinePartitionTableAsync(
        string firmwareOrContainerPath,
        CancellationToken ct = default);

    Task<string> ExportPartitionTableJsonAsync(
        DevicePartitionTableSnapshot snapshot,
        string outputFilePath,
        CancellationToken ct = default);

    Task<PartitionModificationResult> CreateLogicalPartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        long sizeBytes,
        string confirmationText,
        CancellationToken ct = default);

    Task<PartitionModificationResult> ResizeLogicalPartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        long newSizeBytes,
        string confirmationText,
        CancellationToken ct = default);

    Task<PartitionModificationResult> DeleteLogicalPartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        string confirmationText,
        CancellationToken ct = default);

    Task<PartitionModificationResult> ErasePartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        string confirmationText,
        CancellationToken ct = default);

    Task<PartitionModificationResult> BackupPartitionViaAdbAsync(
        ConnectedDeviceSnapshot device,
        DevicePartitionEntry partition,
        string outputDirectory,
        CancellationToken ct = default);

    Task<PartitionModificationResult> FlashVbmetaWithPolicyAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        string vbmetaImagePath,
        bool disableVerity,
        bool disableVerification,
        string confirmationText,
        CancellationToken ct = default);
}

public interface IBootloaderManager
{
    Task<BootloaderAssessment> AssessDeviceAsync(ConnectedDeviceSnapshot device, CancellationToken ct = default);
    Task<BootloaderOperationResult> ExecuteVerifiedUnlockAsync(
        ConnectedDeviceSnapshot device,
        BootloaderAssessment assessment,
        string userConfirmationInput,
        CancellationToken ct = default);
}

public interface IFlashManager
{
    Task<FlashExecutionPlan> BuildExecutionPlanAsync(
        ConnectedDeviceSnapshot device,
        IReadOnlyList<(string PartitionName, string ImageFilePath)> requestedPartitions,
        CancellationToken ct = default);

    Task<DryRunValidationReport> RunDryRunValidationAsync(
        FlashExecutionPlan plan,
        CancellationToken ct = default);

    Task<FlashOperationReport> ExecutePlanAsync(
        FlashExecutionPlan plan,
        DryRunValidationReport dryRunReport,
        FlashConfirmationToken confirmation,
        IProgress<(int StepIndex, int TotalSteps, string Status)>? progress = null,
        CancellationToken ct = default);
}

public interface IAppSettingsService
{
    AppSettingsModel Current { get; }
    Task SaveAsync(AppSettingsModel updated, CancellationToken ct = default);
}

public interface IFirstTimeSetupService
{
    Task<FirstTimeSetupResult> InitializeEnvironmentAsync(
        bool downloadPlatformToolsIfMissing = false,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default);
}

public enum QuickReadTransportFilter
{
    AutoDetectAll = 0,
    MediaTekVcom = 1,
    UnisocVcom = 2,
    AdbAndFastboot = 3
}

public sealed record QuickPhoneReadReport(
    bool Succeeded,
    string DetectedProtocolLabel,
    string PortOrSerial,
    string BrandSummary,
    string ModelSummary,
    DataProvenance ModelProvenance,
    string ChipsetSummary,
    string AndroidOrBootVersion,
    string SecurityAndLockSummary,
    string SlotAndStorageSummary,
    string GuidanceMessage,
    IReadOnlyList<KeyValuePair<string, string>> DetailedFields);

public interface IPhoneQuickReadService
{
    Task<QuickPhoneReadReport> CatchAndReadPhoneAsync(
        QuickReadTransportFilter filter = QuickReadTransportFilter.AutoDetectAll,
        string? preferredBrandHint = null,
        TimeSpan? timeout = null,
        Action<int, int, string>? onStepProgress = null,
        Action<string>? onConsoleLine = null,
        CancellationToken ct = default);
}
