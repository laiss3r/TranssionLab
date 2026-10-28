using System.Collections.ObjectModel;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Firmware;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 11: Firmware Explorer (Section 6 + Direct Partition Extraction & Sparse Conversion)
public sealed class FirmwareExplorerViewModel : PageViewModelBase
{
    private readonly IFirmwareFormatRegistry _registry;
    private readonly FlashManagerViewModel _flashManagerVm;
    private readonly string _stagingDir;

    public override int PageNumber => 11;
    public override string PageTitle => "Firmware Explorer";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "📦";

    private string _selectedFilePath = string.Empty;
    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
                InspectFirmwareCommand.RaiseCanExecuteChanged();
        }
    }

    private FirmwareInspectionResult? _inspectionResult;
    public FirmwareInspectionResult? InspectionResult
    {
        get => _inspectionResult;
        private set
        {
            if (SetProperty(ref _inspectionResult, value))
            {
                OnPropertyChanged(nameof(HasInspectionResult));
                ConvertSparseToRawCommand.RaiseCanExecuteChanged();
                UnpackBootImageCommand.RaiseCanExecuteChanged();
                ExtractSelectedPartitionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasInspectionResult => InspectionResult is not null;

    public ObservableCollection<PartitionImageMetadata> Partitions { get; } = new();
    public ObservableCollection<FirmwareArchiveEntry> ArchiveEntries { get; } = new();
    public ObservableCollection<string> WarningsAndNotes { get; } = new();

    private PartitionImageMetadata? _selectedPartitionRow;
    public PartitionImageMetadata? SelectedPartitionRow
    {
        get => _selectedPartitionRow;
        set
        {
            if (SetProperty(ref _selectedPartitionRow, value))
            {
                ExtractSelectedPartitionCommand.RaiseCanExecuteChanged();
                StageSelectedPartitionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand InspectFirmwareCommand { get; }
    public RelayCommand BrowseFirmwareFileCommand { get; }
    public AsyncRelayCommand ExtractSelectedPartitionCommand { get; }
    public AsyncRelayCommand ConvertSparseToRawCommand { get; }
    public AsyncRelayCommand UnpackBootImageCommand { get; }
    public AsyncRelayCommand StageSelectedPartitionCommand { get; }

    public FirmwareExplorerViewModel(
        IFirmwareFormatRegistry registry,
        FlashManagerViewModel flashManagerVm,
        IAppSettingsService settings)
    {
        _registry = registry;
        _flashManagerVm = flashManagerVm;
        _stagingDir = Path.Combine(settings.Current.WorkspaceDataDirectory, "staging_images");

        InspectFirmwareCommand = new AsyncRelayCommand(
            InspectAsync,
            () => !string.IsNullOrWhiteSpace(SelectedFilePath));

        BrowseFirmwareFileCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke(
                "Select Firmware Package or Partition Image",
                "Supported Firmware (*.zip;*.tar;*.gz;*.tgz;*.bin;*.pac;*.txt;*.xml;*.img)|*.zip;*.tar;*.gz;*.tgz;*.bin;*.pac;*.txt;*.xml;*.img|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                SelectedFilePath = picked;
        });

        ExtractSelectedPartitionCommand = new AsyncRelayCommand(
            ExtractSelectedAsync,
            () => InspectionResult is not null &&
                  SelectedPartitionRow is { IsProtectedNvOrRadio: false } &&
                  (InspectionResult.SuperInfo is not null ||
                   InspectionResult.PackageFormat is
                       FirmwareFormatKind.ZipArchive or
                       FirmwareFormatKind.AndroidOtaZip or
                       FirmwareFormatKind.AndroidPayloadBin or
                       FirmwareFormatKind.AndroidSuperImage or
                       FirmwareFormatKind.TarArchive or
                       FirmwareFormatKind.TarGzipArchive or
                       FirmwareFormatKind.UnisocPacContainer or
                       FirmwareFormatKind.MediaTekScatterText or
                       FirmwareFormatKind.MediaTekScatterXml));

        ConvertSparseToRawCommand = new AsyncRelayCommand(
            ConvertSparseAsync,
            () => InspectionResult is { PackageFormat: FirmwareFormatKind.AndroidSparseImage });

        UnpackBootImageCommand = new AsyncRelayCommand(
            UnpackBootAsync,
            () => InspectionResult is { PackageFormat: FirmwareFormatKind.AndroidBootImage or FirmwareFormatKind.AndroidVendorBootImage });

        StageSelectedPartitionCommand = new AsyncRelayCommand(
            StageSelectedToFlashAsync,
            () => InspectionResult is not null && SelectedPartitionRow is { IsProtectedNvOrRadio: false });

        StatusBanner = $"Supported formats: {string.Join(", ", _registry.SupportedFormats)}";
    }

    private async Task InspectAsync()
    {
        IsBusy = true;
        try
        {
            var res = await _registry.InspectAsync(SelectedFilePath).ConfigureAwait(true);
            InspectionResult = res;

            Partitions.Clear();
            foreach (var p in res.Partitions) Partitions.Add(p);
            SelectedPartitionRow = Partitions.FirstOrDefault(p => !p.IsProtectedNvOrRadio);

            ArchiveEntries.Clear();
            foreach (var a in res.ArchiveEntries) ArchiveEntries.Add(a);

            WarningsAndNotes.Clear();
            foreach (var w in res.ParsingWarnings) WarningsAndNotes.Add($"[Warning] {w}");
            foreach (var d in res.MissingDependencies) WarningsAndNotes.Add($"[Dependency] {d}");

            StatusBanner = $"Inspected '{res.FileName}': Format={res.PackageFormat}, Partitions={res.Partitions.Count}, SHA256={(res.Sha256Hex is { Length: >= 16 } s ? s[..16] + "..." : res.Sha256Hex)}";
        }
        catch (Exception ex)
        {
            StatusBanner = $"Inspection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExtractSelectedAsync()
    {
        if (InspectionResult is null || SelectedPartitionRow is null) return;
        IsBusy = true;
        try
        {
            var outFile = await _registry.ExtractArchiveEntryAsync(
                InspectionResult.SourcePath,
                SelectedPartitionRow.PartitionName,
                _stagingDir).ConfigureAwait(true);
            var msg = $"Extracted '{SelectedPartitionRow.PartitionName}' -> '{outFile}'.";
            LogConsole($"✓ {msg}");
            StatusBanner = msg;
        }
        catch (Exception ex)
        {
            LogConsole($"✗ Extraction failed: {ex.Message}");
            StatusBanner = $"Extraction failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConvertSparseAsync()
    {
        if (InspectionResult is null) return;
        IsBusy = true;
        try
        {
            Directory.CreateDirectory(_stagingDir);
            var rawName = Path.GetFileNameWithoutExtension(InspectionResult.FileName) + ".raw.img";
            var outPath = Path.Combine(_stagingDir, rawName);
            var saved = await _registry.ConvertSparseImageToRawAsync(InspectionResult.SourcePath, outPath).ConfigureAwait(true);
            var msg = $"Converted Sparse image to Raw image -> '{saved}'.";
            LogConsole($"✓ {msg}");
            StatusBanner = msg;
        }
        catch (Exception ex)
        {
            LogConsole($"✗ Sparse conversion failed: {ex.Message}");
            StatusBanner = $"Sparse conversion failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UnpackBootAsync()
    {
        if (InspectionResult is null) return;
        IsBusy = true;
        try
        {
            var subDir = Path.Combine(_stagingDir, $"{Path.GetFileNameWithoutExtension(InspectionResult.FileName)}_unpacked");
            var report = await _registry.UnpackBootOrVendorBootImageAsync(InspectionResult.SourcePath, subDir).ConfigureAwait(true);
            LogConsole($"✓ {report.Summary}");
            StatusBanner = report.Summary;
        }
        catch (Exception ex)
        {
            LogConsole($"✗ Boot unpack failed: {ex.Message}");
            StatusBanner = $"Boot unpack failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StageSelectedToFlashAsync()
    {
        if (InspectionResult is null || SelectedPartitionRow is null) return;
        IsBusy = true;
        try
        {
            string imagePath;
            if (InspectionResult.SuperInfo is null &&
                InspectionResult.PackageFormat is
                    FirmwareFormatKind.AndroidSparseImage or
                    FirmwareFormatKind.AndroidBootImage or
                    FirmwareFormatKind.AndroidVendorBootImage or
                    FirmwareFormatKind.AndroidVbmetaImage or
                    FirmwareFormatKind.RawPartitionImage)
            {
                imagePath = InspectionResult.SourcePath;
            }
            else
            {
                imagePath = await _registry.ExtractArchiveEntryAsync(
                    InspectionResult.SourcePath,
                    SelectedPartitionRow.PartitionName,
                    _stagingDir).ConfigureAwait(true);
            }

            _flashManagerVm.EnqueuePartitionImage(SelectedPartitionRow.PartitionName, imagePath);
            StatusBanner = $"Staged '{SelectedPartitionRow.PartitionName}' ({Path.GetFileName(imagePath)}) into Flash Manager (#14).";
        }
        catch (Exception ex)
        {
            StatusBanner = $"Staging failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// Page 12: ROM Studio (Section 6 + payload.bin, Sparse-to-Raw & Active Slot Control)
public sealed class RomStudioViewModel : PageViewModelBase
{
    private readonly RomStudioService _romStudio;
    private readonly IFirmwareFormatRegistry _firmwareRegistry;
    private readonly IDeviceDiscoveryProvider? _discovery;
    private readonly IFastbootProvider? _fastboot;

    public override int PageNumber => 12;
    public override string PageTitle => "Transsion ROM Studio";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "🔧";

    private string _stockArchivePath = string.Empty;
    public string StockArchivePath
    {
        get => _stockArchivePath;
        set
        {
            if (SetProperty(ref _stockArchivePath, value))
                ExtractEntryCommand.RaiseCanExecuteChanged();
        }
    }

    private string _entryToExtract = "boot";
    public string EntryToExtract
    {
        get => _entryToExtract;
        set
        {
            if (SetProperty(ref _entryToExtract, value))
                ExtractEntryCommand.RaiseCanExecuteChanged();
        }
    }

    private string _stagingOutputDirectory;
    public string StagingOutputDirectory
    {
        get => _stagingOutputDirectory;
        set => SetProperty(ref _stagingOutputDirectory, value);
    }

    private string _sparseInputPath = string.Empty;
    public string SparseInputPath
    {
        get => _sparseInputPath;
        set
        {
            if (SetProperty(ref _sparseInputPath, value))
                UnsparseImageCommand.RaiseCanExecuteChanged();
        }
    }

    private string _aospWorkspacePath = string.Empty;
    public string AospWorkspacePath
    {
        get => _aospWorkspacePath;
        set => SetProperty(ref _aospWorkspacePath, value);
    }

    private string _activeSlotStatusText = "Connect device in Fastboot / Fastbootd to switch Slot A or Slot B.";
    public string ActiveSlotStatusText
    {
        get => _activeSlotStatusText;
        private set => SetProperty(ref _activeSlotStatusText, value);
    }

    private RomBuildEnvironmentReport? _buildEnvReport;
    public RomBuildEnvironmentReport? BuildEnvReport
    {
        get => _buildEnvReport;
        private set => SetProperty(ref _buildEnvReport, value);
    }

    public ObservableCollection<string> ExtractedFiles { get; } = new();

    public AsyncRelayCommand ExtractEntryCommand { get; }
    public AsyncRelayCommand UnsparseImageCommand { get; }
    public AsyncRelayCommand SetSlotACommand { get; }
    public AsyncRelayCommand SetSlotBCommand { get; }
    public RelayCommand ValidateSourceBuildEnvCommand { get; }
    public RelayCommand BrowseStockArchiveCommand { get; }
    public RelayCommand BrowseOutputDirectoryCommand { get; }
    public RelayCommand BrowseSparseInputCommand { get; }
    public RelayCommand BrowseAospWorkspaceCommand { get; }

    public RomStudioViewModel(
        RomStudioService romStudio,
        IFirmwareFormatRegistry firmwareRegistry,
        IAppSettingsService settings,
        IDeviceDiscoveryProvider? discovery = null,
        IFastbootProvider? fastboot = null)
    {
        _romStudio = romStudio;
        _firmwareRegistry = firmwareRegistry;
        _discovery = discovery;
        _fastboot = fastboot;
        _stagingOutputDirectory = Path.Combine(settings.Current.WorkspaceDataDirectory, "staging_images");

        BrowseStockArchiveCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke(
                "Select Stock Firmware Container (.zip / OTA .zip / payload.bin / .tar / .pac / scatter)",
                "Firmware Containers (*.zip;*.bin;*.tar;*.gz;*.pac;*.txt;*.xml;*.img)|*.zip;*.bin;*.tar;*.gz;*.pac;*.txt;*.xml;*.img|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                StockArchivePath = picked;
        });

        BrowseOutputDirectoryCommand = new RelayCommand(() =>
        {
            var dir = UiInteractionBridge.PickDirectory?.Invoke("Select Destination Output Folder");
            if (!string.IsNullOrWhiteSpace(dir))
                StagingOutputDirectory = dir;
        });

        BrowseSparseInputCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke(
                "Select Android Sparse Image (0xED26FF3A)",
                "Sparse Images (*.img;*.bin)|*.img;*.bin|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                SparseInputPath = picked;
        });

        BrowseAospWorkspaceCommand = new RelayCommand(() =>
        {
            var dir = UiInteractionBridge.PickDirectory?.Invoke("Select AOSP / LineageOS Source Workspace");
            if (!string.IsNullOrWhiteSpace(dir))
                AospWorkspacePath = dir;
        });

        ExtractEntryCommand = new AsyncRelayCommand(
            async () =>
            {
                IsBusy = true;
                try
                {
                    var extracted = await _romStudio.ExtractStockPartitionsForFastbootAsync(
                        StockArchivePath, [EntryToExtract], StagingOutputDirectory).ConfigureAwait(true);
                    foreach (var f in extracted) ExtractedFiles.Add(f);
                    StatusBanner = extracted.Count > 0
                        ? $"Extracted & verified -> '{extracted[0]}'."
                        : "Skipped protected NVRAM/radio entry.";
                }
                catch (Exception ex)
                {
                    StatusBanner = $"Extraction error: {ex.Message}";
                }
                finally
                {
                    IsBusy = false;
                }
            },
            () => !string.IsNullOrWhiteSpace(StockArchivePath) && !string.IsNullOrWhiteSpace(EntryToExtract));

        UnsparseImageCommand = new AsyncRelayCommand(
            async () =>
            {
                IsBusy = true;
                try
                {
                    Directory.CreateDirectory(StagingOutputDirectory);
                    var rawOut = Path.Combine(StagingOutputDirectory, Path.GetFileNameWithoutExtension(SparseInputPath) + ".raw.img");
                    var saved = await _firmwareRegistry.ConvertSparseImageToRawAsync(SparseInputPath, rawOut).ConfigureAwait(true);
                    ExtractedFiles.Add(saved);
                    StatusBanner = $"Unsparsed image -> '{saved}'.";
                }
                catch (Exception ex)
                {
                    StatusBanner = $"Sparse conversion error: {ex.Message}";
                }
                finally
                {
                    IsBusy = false;
                }
            },
            () => !string.IsNullOrWhiteSpace(SparseInputPath));

        SetSlotACommand = new AsyncRelayCommand(() => SwitchSlotAsync("a"));
        SetSlotBCommand = new AsyncRelayCommand(() => SwitchSlotAsync("b"));

        ValidateSourceBuildEnvCommand = new RelayCommand(() =>
        {
            try
            {
                BuildEnvReport = _romStudio.ValidateCustomRomSourceWorkspace(AospWorkspacePath);
                StatusBanner = BuildEnvReport.ReadyForSourceBuild
                    ? "Workspace satisfies AOSP/LineageOS custom ROM source build prerequisites."
                    : $"Source build workspace incomplete ({BuildEnvReport.ValidationFindings.Count} requirement finding(s)).";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Workspace validation error: {ex.Message}";
            }
        });

        StatusBanner = "Ready. Select target firmware container & destination folder, or switch active Slot A / Slot B in Fastboot.";
    }

    private async Task SwitchSlotAsync(string slot)
    {
        if (_discovery is null || _fastboot is null)
        {
            ActiveSlotStatusText = "Fastboot slot switching requires live transport services.";
            return;
        }

        await RunSafeActivityAsync(async () =>
        {
            ConsoleLineSink?.Invoke($"▶ Operation: Switch Active Boot Slot -> Slot {slot.ToUpperInvariant()}");
            ConsoleLineSink?.Invoke("📋 Instructions: Connect device in Fastboot or Fastbootd mode.");
            ConsoleLineSink?.Invoke("Waiting for USB device...");
            var dev = await _discovery.WaitForDeviceAsync(
                $"Set Active Slot {slot.ToUpperInvariant()}",
                [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

            var serial = dev.TransportSerial ?? dev.DeviceKey;
            var res = await _fastboot.SetActiveSlotAsync(serial, slot).ConfigureAwait(true);
            ActiveSlotStatusText = res.Succeeded
                ? $"✓ Active slot switched to Slot {slot.ToUpperInvariant()} on {serial}."
                : $"Slot {slot.ToUpperInvariant()} command failed on {serial}: {res.StandardError}";
            StatusBanner = ActiveSlotStatusText;
            ConsoleLineSink?.Invoke(ActiveSlotStatusText);
        }, $"Set Slot {slot.ToUpperInvariant()} failed").ConfigureAwait(true);
    }
}

public sealed class FlashQueueRow : ObservableObject
{
    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public required string PartitionName { get; init; }
    public required string ImageFilePath { get; init; }
}

// Page 14: Safe 11-Step Flash Manager (Section 7)
public sealed class FlashManagerViewModel : PageViewModelBase, IDisposable
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IFlashManager _flashManager;
    private CancellationTokenSource? _flashCts;

    public override int PageNumber => 14;
    public override string PageTitle => "Fastboot OGT Flasher";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "🔥";

    private string _newPartitionName = "boot_a";
    public string NewPartitionName { get => _newPartitionName; set => SetProperty(ref _newPartitionName, value); }

    private string _newImageFilePath = string.Empty;
    public string NewImageFilePath
    {
        get => _newImageFilePath;
        set
        {
            if (SetProperty(ref _newImageFilePath, value))
                AddToQueueCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<FlashQueueRow> FlashQueue { get; } = new();

    private FlashQueueRow? _selectedQueueRow;
    public FlashQueueRow? SelectedQueueRow
    {
        get => _selectedQueueRow;
        set
        {
            if (SetProperty(ref _selectedQueueRow, value))
                RemoveSelectedRowCommand.RaiseCanExecuteChanged();
        }
    }

    private FlashExecutionPlan? _activePlan;
    public FlashExecutionPlan? ActivePlan
    {
        get => _activePlan;
        private set
        {
            if (SetProperty(ref _activePlan, value))
            {
                RunDryRunCommand.RaiseCanExecuteChanged();
                ExecuteFlashCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private DryRunValidationReport? _dryRunReport;
    public DryRunValidationReport? DryRunReport
    {
        get => _dryRunReport;
        private set
        {
            if (SetProperty(ref _dryRunReport, value))
                ExecuteFlashCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _riskAcknowledged;
    public bool RiskAcknowledged
    {
        get => _riskAcknowledged;
        set
        {
            if (SetProperty(ref _riskAcknowledged, value))
                ExecuteFlashCommand.RaiseCanExecuteChanged();
        }
    }

    private string _confirmationText = string.Empty;
    public string ConfirmationText
    {
        get => _confirmationText;
        set
        {
            if (SetProperty(ref _confirmationText, value))
                ExecuteFlashCommand.RaiseCanExecuteChanged();
        }
    }

    private int _progressPercent;
    public int ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    private FlashOperationReport? _lastOperationReport;
    public FlashOperationReport? LastOperationReport
    {
        get => _lastOperationReport;
        private set => SetProperty(ref _lastOperationReport, value);
    }

    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand RemoveSelectedRowCommand { get; }
    public RelayCommand BrowseFlashImageCommand { get; }
    public RelayCommand SelectAllQueueCommand { get; }
    public RelayCommand UnselectAllQueueCommand { get; }
    public RelayCommand ImportFirmwareFolderCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand BuildPlanCommand { get; }
    public AsyncRelayCommand RunDryRunCommand { get; }
    public AsyncRelayCommand ExecuteFlashCommand { get; }
    public RelayCommand CancelBetweenPartitionsCommand { get; }

    public FlashManagerViewModel(IDeviceDiscoveryProvider discovery, IFlashManager flashManager)
    {
        _discovery = discovery;
        _flashManager = flashManager;

        BuildPlanCommand = new AsyncRelayCommand(BuildPlanAsync, () => FlashQueue.Any(q => q.IsEnabled));
        RunDryRunCommand = new AsyncRelayCommand(RunDryRunAsync, () => ActivePlan is { BlockedBySafetyPolicy: false });
        ExecuteFlashCommand = new AsyncRelayCommand(
            ExecuteVerifiedFlashAsync,
            () => ActivePlan is { BlockedBySafetyPolicy: false } &&
                  DryRunReport is { Passed: true } &&
                  RiskAcknowledged &&
                  string.Equals(ConfirmationText?.Trim(), ActivePlan.RequiredConfirmationText, StringComparison.Ordinal));

        CancelBetweenPartitionsCommand = new RelayCommand(
            () => _flashCts?.Cancel(),
            () => IsBusy);

        SelectAllQueueCommand = new RelayCommand(() =>
        {
            foreach (var row in FlashQueue) row.IsEnabled = true;
            BuildPlanCommand.RaiseCanExecuteChanged();
            StatusBanner = $"Selected all {FlashQueue.Count} queued partition(s).";
        });

        UnselectAllQueueCommand = new RelayCommand(() =>
        {
            foreach (var row in FlashQueue) row.IsEnabled = false;
            BuildPlanCommand.RaiseCanExecuteChanged();
            StatusBanner = "Unselected all queued partitions.";
        });

        ImportFirmwareFolderCommand = new RelayCommand(() =>
        {
            var folder = UiInteractionBridge.PickDirectory?.Invoke("Select Unpacked Firmware Folder (.img files)");
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                ImportUnprotectedImagesFromDirectory(folder);
            }
        });

        BrowseFlashImageCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke(
                "Select Partition Image (.img / .bin)",
                "Partition Images (*.img;*.bin)|*.img;*.bin|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                NewImageFilePath = picked;
        });

        AddToQueueCommand = new RelayCommand(
            () => EnqueuePartitionImage(NewPartitionName, NewImageFilePath),
            () => !string.IsNullOrWhiteSpace(NewPartitionName) && !string.IsNullOrWhiteSpace(NewImageFilePath));

        RemoveSelectedRowCommand = new RelayCommand(
            () =>
            {
                if (SelectedQueueRow is not null)
                {
                    FlashQueue.Remove(SelectedQueueRow);
                    ActivePlan = null;
                    DryRunReport = null;
                    BuildPlanCommand.RaiseCanExecuteChanged();
                }
            },
            () => SelectedQueueRow is not null);

        ClearQueueCommand = new RelayCommand(() =>
        {
            FlashQueue.Clear();
            ActivePlan = null;
            DryRunReport = null;
            BuildPlanCommand.RaiseCanExecuteChanged();
            StatusBanner = "Cleared flash queue.";
        });
    }

    public void EnqueuePartitionImage(string partitionName, string imagePath)
    {
        var row = new FlashQueueRow
        {
            IsEnabled = true,
            PartitionName = partitionName.Trim(),
            ImageFilePath = imagePath.Trim()
        };
        row.PropertyChanged += (_, _) => BuildPlanCommand.RaiseCanExecuteChanged();
        FlashQueue.Add(row);
        ActivePlan = null;
        DryRunReport = null;
        BuildPlanCommand.RaiseCanExecuteChanged();
        StatusBanner = $"Queued '{partitionName.Trim()}' ({FlashQueue.Count} item(s)). Build Plan next.";
    }

    public int ImportUnprotectedImagesFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return 0;

        int added = 0;
        foreach (var imgPath in Directory.GetFiles(directoryPath, "*.img", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var partName = Path.GetFileNameWithoutExtension(imgPath).Trim();
            if (string.IsNullOrWhiteSpace(partName) ||
                HashAndBinaryHelpers.IsProtectedPartition(partName) ||
                string.Equals(partName, "userdata", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FlashQueue.Any(q => string.Equals(q.PartitionName, partName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            EnqueuePartitionImage(partName, imgPath);
            added++;
        }

        StatusBanner = added > 0
            ? $"Imported {added} non-protected .img partition(s) from '{directoryPath}'."
            : $"No new non-protected .img partitions found in '{directoryPath}'.";
        ConsoleLineSink?.Invoke($"• Fastboot OGT Flasher: {StatusBanner}");
        return added;
    }

    public void EmergencyStop()
    {
        try { _flashCts?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        _flashCts?.Dispose();
        _flashCts = null;
    }

    private async Task BuildPlanAsync()
    {
        await RunSafeActivityAsync(async () =>
        {
            var activeRows = FlashQueue.Where(q => q.IsEnabled).ToList();
            if (activeRows.Count == 0)
            {
                StatusBanner = "Select at least one partition row (✓) before building an execution plan.";
                return;
            }

            var dev = await _discovery.WaitForDeviceAsync(
                "Build & Validate Flash Execution Plan",
                [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

            DryRunReport = null;
            StatusBanner = $"Validating {activeRows.Count} partition image(s) against {dev.TransportSerial} ({dev.ActiveConnectionMode})...";
            var tuples = activeRows.Select(q => (q.PartitionName, q.ImageFilePath)).ToList();
            ActivePlan = await _flashManager.BuildExecutionPlanAsync(dev, tuples).ConfigureAwait(true);
            StatusBanner = ActivePlan.BlockedBySafetyPolicy
                ? $"Plan blocked by safety guardrails: {(ActivePlan.SafetyFindings.Count > 0 ? ActivePlan.SafetyFindings[0] : "Policy violation")}"
                : $"Plan {ActivePlan.PlanId} ready ({ActivePlan.ProposedSteps.Count} steps). Run Dry-Run Validation next.";
        }, "Flash plan build failed").ConfigureAwait(true);
    }

    private async Task RunDryRunAsync()
    {
        if (ActivePlan is null) return;
        await RunSafeActivityAsync(async () =>
        {
            await _discovery.WaitForDeviceAsync(
                "Dry-Run Pre-Flight Check",
                [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                TimeSpan.FromSeconds(20),
                CreateWaitProgressCallback()).ConfigureAwait(true);

            StatusBanner = $"Running 11-step Dry-Run validation for {ActivePlan.PlanId}...";
            DryRunReport = await _flashManager.RunDryRunValidationAsync(ActivePlan).ConfigureAwait(true);
            StatusBanner = DryRunReport.Passed
                ? $"Dry-Run PASSED. Confirm risk and type '{ActivePlan.RequiredConfirmationText}' to enable Write."
                : $"Dry-Run FAILED: {string.Join("; ", DryRunReport.BlockingErrors)}";
        }, "Dry-Run validation failed").ConfigureAwait(true);
    }

    private async Task ExecuteVerifiedFlashAsync()
    {
        if (ActivePlan is null || DryRunReport is null) return;
        IsBusy = true;
        CancelBetweenPartitionsCommand.RaiseCanExecuteChanged();
        _flashCts = new CancellationTokenSource();

        try
        {
            await _discovery.WaitForDeviceAsync(
                "Execute Validated Flash Plan",
                [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                TimeSpan.FromSeconds(25),
                CreateWaitProgressCallback(),
                _flashCts.Token).ConfigureAwait(true);
            IsWaitingForDevice = false;

            var token = new FlashConfirmationToken(
                ActivePlan.PlanId,
                ActivePlan.PlanHashSha256,
                ConfirmationText.Trim(),
                RiskAcknowledged,
                DateTimeOffset.UtcNow);

            var prog = new Progress<(int StepIndex, int TotalSteps, string Status)>(p =>
                UiInteractionBridge.RunOnUiThread(() =>
                {
                    ProgressPercent = p.TotalSteps > 0 ? (p.StepIndex * 100) / p.TotalSteps : 0;
                    StatusBanner = $"[{p.StepIndex}/{p.TotalSteps}] {p.Status}";
                }));

            LastOperationReport = await _flashManager.ExecutePlanAsync(
                ActivePlan, DryRunReport, token, prog, _flashCts.Token).ConfigureAwait(true);

            StatusBanner = LastOperationReport.VerificationSummary;
            ConfirmationText = string.Empty;
            RiskAcknowledged = false;
        }
        catch (OperationCanceledException)
        {
            StatusBanner = "Flash cancelled safely by user.";
        }
        catch (Exception ex)
        {
            StatusBanner = $"Flash aborted: {ex.Message}";
            UiInteractionBridge.OnCommandException?.Invoke(ex);
        }
        finally
        {
            IsWaitingForDevice = false;
            _flashCts.Dispose();
            _flashCts = null;
            IsBusy = false;
            CancelBetweenPartitionsCommand.RaiseCanExecuteChanged();
        }
    }
}

// Page 15: Device Profiles Catalog, JSON Importer & Bench Verification Sign-Off
public sealed class DeviceProfilesViewModel : PageViewModelBase
{
    private readonly IDeviceProfileRepository _repo;
    private readonly IDeviceDiscoveryProvider _discovery;

    public override int PageNumber => 15;
    public override string PageTitle => "Device Profiles";
    public override string NavigationGroup => "PROFILES & TELEMETRY";
    public override string IconGlyph => "📱";

    private string _searchFilter = string.Empty;
    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
                RefreshProfilesList();
        }
    }

    private string _importJsonPath = string.Empty;
    public string ImportJsonPath
    {
        get => _importJsonPath;
        set
        {
            if (SetProperty(ref _importJsonPath, value))
                ImportProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<DeviceProfile> FilteredProfiles { get; } = new();

    private DeviceProfile? _selectedProfile;
    public DeviceProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                PromoteSelectedProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _benchTesterName = Environment.UserName;
    public string BenchTesterName
    {
        get => _benchTesterName;
        set
        {
            if (SetProperty(ref _benchTesterName, value))
                PromoteSelectedProfileCommand.RaiseCanExecuteChanged();
        }
    }

    private string _benchTestedFingerprint = string.Empty;
    public string BenchTestedFingerprint
    {
        get => _benchTestedFingerprint;
        set
        {
            if (SetProperty(ref _benchTestedFingerprint, value))
                PromoteSelectedProfileCommand.RaiseCanExecuteChanged();
        }
    }

    private string _benchNotes = "Verified live ADB getprop, Fastboot variables, and partition table on physical handset.";
    public string BenchNotes
    {
        get => _benchNotes;
        set => SetProperty(ref _benchNotes, value);
    }

    public AsyncRelayCommand ImportProfileCommand { get; }
    public RelayCommand BrowseProfileJsonCommand { get; }
    public RelayCommand AutoFillFingerprintFromDeviceCommand { get; }
    public AsyncRelayCommand PromoteSelectedProfileCommand { get; }

    public DeviceProfilesViewModel(IDeviceProfileRepository repo, IDeviceDiscoveryProvider discovery)
    {
        _repo = repo;
        _discovery = discovery;

        BrowseProfileJsonCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke("Select Device Profile JSON", "JSON Files (*.json)|*.json|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                ImportJsonPath = picked;
        });

        ImportProfileCommand = new AsyncRelayCommand(
            async () =>
            {
                try
                {
                    var imported = await _repo.ImportProfileFromFileAsync(ImportJsonPath).ConfigureAwait(true);
                    RefreshProfilesList();
                    SelectedProfile = imported;
                    StatusBanner = $"Imported profile '{imported.MarketingName}' ({imported.ProfileId}).";
                }
                catch (Exception ex)
                {
                    StatusBanner = $"Import failed: {ex.Message}";
                }
            },
            () => !string.IsNullOrWhiteSpace(ImportJsonPath));

        AutoFillFingerprintFromDeviceCommand = new RelayCommand(() =>
        {
            var dev = _discovery.SelectedDevice;
            if (dev?.BuildFingerprint.Value is { Length: > 0 } fp)
            {
                BenchTestedFingerprint = fp;
                StatusBanner = $"Captured verified build fingerprint '{fp}' from connected device.";
            }
            else
            {
                StatusBanner = "Connect device via Authorized ADB first to auto-capture ro.build.fingerprint, or paste the verified fingerprint.";
            }
        });

        PromoteSelectedProfileCommand = new AsyncRelayCommand(
            async () =>
            {
                if (SelectedProfile is null) return;
                try
                {
                    var promoted = SelectedProfile with
                    {
                        VerificationLevel = HardwareVerificationLevel.HardwareVerified,
                        HardwareVerification = new HardwareVerificationRecord(
                            IsHardwareVerified: true,
                            TestedBy: BenchTesterName.Trim(),
                            TestedAtUtc: DateTimeOffset.UtcNow,
                            TestedBuildFingerprint: BenchTestedFingerprint.Trim(),
                            VerificationNotes: BenchNotes.Trim())
                    };
                    var saved = await _repo.SaveOrPromoteProfileAsync(promoted).ConfigureAwait(true);
                    RefreshProfilesList();
                    SelectedProfile = FilteredProfiles.FirstOrDefault(p => p.ProfileId == saved.ProfileId);
                    StatusBanner = $"Saved bench-verified profile '{saved.MarketingName}' ({saved.ProfileId}) to local profiles directory.";
                }
                catch (Exception ex)
                {
                    StatusBanner = $"Bench sign-off failed: {ex.Message}";
                }
            },
            () => SelectedProfile is not null &&
                  !string.IsNullOrWhiteSpace(BenchTesterName) &&
                  !string.IsNullOrWhiteSpace(BenchTestedFingerprint));

        RefreshProfilesList();
    }

    public void RefreshProfilesList()
    {
        var term = SearchFilter?.Trim() ?? string.Empty;
        FilteredProfiles.Clear();
        foreach (var p in _repo.GetAllProfiles())
        {
            if (string.IsNullOrEmpty(term) ||
                p.MarketingName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Manufacturer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Codename.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.ChipsetFamily.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.ModelCodes.Any(m => m.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredProfiles.Add(p);
            }
        }
        SelectedProfile ??= FilteredProfiles.FirstOrDefault();
        StatusBanner = $"{FilteredProfiles.Count} profile(s) loaded. Reference profiles stay ReferenceSpecification until signed off with a physical bench fingerprint.";
    }
}
