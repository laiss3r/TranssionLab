using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Firmware.Parsers;

namespace TranssionLab.Firmware;

/// <summary>
/// Format registry, binary inspector, payload extractor, and sparse-to-raw converter for Firmware Explorer & Partition Manager.
/// </summary>
public sealed class FirmwareFormatRegistry : IFirmwareFormatRegistry
{
    private readonly IDeviceProfileRepository _profileRepo;
    private readonly IStructuredLogger _logger;

    public IReadOnlyList<FirmwareFormatKind> SupportedFormats { get; } =
    [
        FirmwareFormatKind.ZipArchive,
        FirmwareFormatKind.AndroidOtaZip,
        FirmwareFormatKind.TarArchive,
        FirmwareFormatKind.TarGzipArchive,
        FirmwareFormatKind.AndroidPayloadBin,
        FirmwareFormatKind.MediaTekScatterText,
        FirmwareFormatKind.MediaTekScatterXml,
        FirmwareFormatKind.UnisocPacContainer,
        FirmwareFormatKind.AndroidSparseImage,
        FirmwareFormatKind.AndroidSuperImage,
        FirmwareFormatKind.AndroidBootImage,
        FirmwareFormatKind.AndroidVendorBootImage,
        FirmwareFormatKind.AndroidVbmetaImage,
        FirmwareFormatKind.RawPartitionImage
    ];

    public FirmwareFormatRegistry(IDeviceProfileRepository profileRepo, IStructuredLogger logger)
    {
        _profileRepo = profileRepo;
        _logger = logger;
    }

    public FirmwareFormatKind DetectFormat(string filePath)
    {
        if (!File.Exists(filePath))
            return FirmwareFormatKind.Unsupported;

        using var fs = File.OpenRead(filePath);
        return HashAndBinaryHelpers.SniffStreamMagic(fs, Path.GetFileName(filePath)).Kind;
    }

    public async Task<FirmwareInspectionResult> InspectAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new ServicingException("FIRMWARE_NOT_FOUND", $"Firmware file does not exist: {filePath}");
        }

        var fi = new FileInfo(filePath);
        FirmwareFormatKind kind;
        string formatSummary;
        using (var fs = File.OpenRead(filePath))
        {
            (kind, formatSummary) = HashAndBinaryHelpers.SniffStreamMagic(fs, fi.Name);
        }

        var (sha256, crc32) = await HashAndBinaryHelpers.ComputeHashesAsync(filePath, ct: ct).ConfigureAwait(false);
        var warnings = new List<string>();
        var missingDeps = new List<string>();
        var archiveEntries = new List<FirmwareArchiveEntry>();
        var partitions = new List<PartitionImageMetadata>();

        SparseImageHeaderInfo? sparseInfo = null;
        BootImageHeaderInfo? bootInfo = null;
        PayloadManifestInfo? payloadInfo = null;
        UnisocPacHeaderInfo? pacInfo = null;
        MediaTekScatterInfo? scatterInfo = null;
        SuperMetadataInfo? superInfo = null;
        string? chipsetHint = null;
        string? modelHint = null;
        string? fpHint = null;

        switch (kind)
        {
            case FirmwareFormatKind.ZipArchive:
            {
                var (specificKind, zEntries, zParts, embPayload, devHint, postBuild, zWarn) =
                    TarAndZipArchiveInspector.InspectZip(filePath);
                kind = specificKind;
                archiveEntries.AddRange(zEntries);
                partitions.AddRange(zParts);
                payloadInfo = embPayload;
                modelHint = devHint;
                fpHint = postBuild;
                warnings.AddRange(zWarn);
                break;
            }

            case FirmwareFormatKind.TarArchive:
            case FirmwareFormatKind.TarGzipArchive:
            {
                var (tEntries, tParts, tWarn) = TarAndZipArchiveInspector.InspectTarOrTarGz(
                    filePath,
                    isGzip: kind == FirmwareFormatKind.TarGzipArchive);
                archiveEntries.AddRange(tEntries);
                partitions.AddRange(tParts);
                warnings.AddRange(tWarn);
                break;
            }

            case FirmwareFormatKind.AndroidPayloadBin:
            {
                using var fs = File.OpenRead(filePath);
                payloadInfo = AndroidPayloadBinParser.TryParsePayloadHeaderAndManifest(fs);
                if (payloadInfo is null)
                {
                    warnings.Add("CrAU header detected, but DeltaArchiveManifest could not be decoded or exceeds inspection bounds.");
                }
                else
                {
                    foreach (var p in payloadInfo.Partitions)
                    {
                        partitions.Add(new PartitionImageMetadata(
                            p.PartitionName,
                            fi.Name,
                            (long)p.NewPartitionBytes,
                            (long)p.NewPartitionBytes,
                            FirmwareFormatKind.AndroidPayloadBin,
                            p.Sha256Hex,
                            null,
                            null,
                            !HashAndBinaryHelpers.IsProtectedPartition(p.PartitionName),
                            HashAndBinaryHelpers.IsProtectedPartition(p.PartitionName),
                            $"Payload Partition ({p.InstallOperationCount} install ops, block={payloadInfo.BlockSize})"));
                    }
                }
                break;
            }

            case FirmwareFormatKind.MediaTekScatterText:
            case FirmwareFormatKind.MediaTekScatterXml:
            {
                var (sInfo, sParts, sWarn) = MediaTekAndUnisocParsers.ParseMediaTekScatter(filePath);
                scatterInfo = sInfo;
                chipsetHint = sInfo.PlatformChipset;
                modelHint = sInfo.ProjectName;
                partitions.AddRange(sParts);
                warnings.AddRange(sWarn);
                missingDeps.Add("Full scatter BROM/Preloader flashing on SLA/DAA-enabled Transsion devices requires an OEM-signed Download Agent (DA); individual unlocked partitions can be flashed via Fastboot/Fastbootd.");
                break;
            }

            case FirmwareFormatKind.UnisocPacContainer:
            {
                using var fs = File.OpenRead(filePath);
                var (pHdr, pParts, _, pWarn) = MediaTekAndUnisocParsers.ParseUnisocPac(fs);
                pacInfo = pHdr;
                modelHint = pHdr?.ProductName;
                partitions.AddRange(pParts);
                warnings.AddRange(pWarn);
                if (pHdr is not null && (!pHdr.HasSignedFdl1 || !pHdr.HasSignedFdl2))
                {
                    missingDeps.Add("PAC container is missing FDL1 or FDL2 entries required for ResearchDownload/FactoryDownload execution.");
                }
                break;
            }

            case FirmwareFormatKind.AndroidSparseImage:
            {
                using var fs = File.OpenRead(filePath);
                sparseInfo = AndroidSparseAndBootParsers.TryParseSparseImage(fs);
                superInfo = AndroidSparseAndBootParsers.TryParseSuperLpMetadata(fs);
                var partName = Path.GetFileNameWithoutExtension(fi.Name);
                long unsparsed = sparseInfo?.TotalUnsparsedBytes ?? fi.Length;
                if (sparseInfo is { AccountingValid: false })
                {
                    warnings.Add("Sparse chunk header block accounting did not match total_blks; image may be truncated.");
                }
                partitions.Add(new PartitionImageMetadata(
                    partName,
                    fi.Name,
                    fi.Length,
                    unsparsed,
                    kind,
                    sha256,
                    null,
                    null,
                    !HashAndBinaryHelpers.IsProtectedPartition(partName),
                    HashAndBinaryHelpers.IsProtectedPartition(partName),
                    $"Sparse v{sparseInfo?.MajorVersion}.{sparseInfo?.MinorVersion} ({sparseInfo?.TotalChunks} chunks -> {unsparsed:N0} B raw)"));

                if (superInfo is not null)
                {
                    foreach (var lp in superInfo.LogicalPartitions)
                    {
                        partitions.Add(new PartitionImageMetadata(
                            lp.PartitionName,
                            $"{fi.Name} (super logical)",
                            lp.SizeBytes,
                            lp.SizeBytes,
                            FirmwareFormatKind.AndroidSuperImage,
                            null,
                            null,
                            null,
                            true,
                            false,
                            $"Dynamic Super Logical Partition [group={lp.GroupName}, extents={lp.ExtentCount}]"));
                    }
                }
                break;
            }

            case FirmwareFormatKind.AndroidSuperImage:
            {
                using var fs = File.OpenRead(filePath);
                superInfo = AndroidSparseAndBootParsers.TryParseSuperLpMetadata(fs);
                partitions.Add(new PartitionImageMetadata(
                    "super",
                    fi.Name,
                    fi.Length,
                    fi.Length,
                    kind,
                    sha256,
                    null,
                    null,
                    true,
                    false,
                    superInfo is not null
                        ? $"Super LpMetadata v{superInfo.MajorVersion}.{superInfo.MinorVersion} ({superInfo.LogicalPartitions.Count} logical partitions)"
                        : formatSummary));

                if (superInfo is not null)
                {
                    foreach (var lp in superInfo.LogicalPartitions)
                    {
                        partitions.Add(new PartitionImageMetadata(
                            lp.PartitionName,
                            $"{fi.Name} (super logical)",
                            lp.SizeBytes,
                            lp.SizeBytes,
                            FirmwareFormatKind.AndroidSuperImage,
                            null,
                            null,
                            null,
                            true,
                            false,
                            $"Dynamic Super Logical Partition [group={lp.GroupName}, extents={lp.ExtentCount}]"));
                    }
                }
                break;
            }

            case FirmwareFormatKind.AndroidBootImage:
            case FirmwareFormatKind.AndroidVendorBootImage:
            {
                using var fs = File.OpenRead(filePath);
                bootInfo = AndroidSparseAndBootParsers.TryParseBootOrVendorBoot(fs);
                var partName = Path.GetFileNameWithoutExtension(fi.Name);
                partitions.Add(new PartitionImageMetadata(
                    partName,
                    fi.Name,
                    fi.Length,
                    fi.Length,
                    kind,
                    sha256,
                    null,
                    null,
                    true,
                    false,
                    bootInfo is not null
                        ? $"{bootInfo.HeaderMagic} v{bootInfo.HeaderVersion} (OS {bootInfo.DecodedOsVersion}, Patch {bootInfo.DecodedSecurityPatch})"
                        : formatSummary));
                break;
            }

            case FirmwareFormatKind.AndroidVbmetaImage:
            case FirmwareFormatKind.RawPartitionImage:
            {
                var partName = Path.GetFileNameWithoutExtension(fi.Name);
                partitions.Add(new PartitionImageMetadata(
                    partName,
                    fi.Name,
                    fi.Length,
                    fi.Length,
                    kind,
                    sha256,
                    null,
                    null,
                    !HashAndBinaryHelpers.IsProtectedPartition(partName),
                    HashAndBinaryHelpers.IsProtectedPartition(partName),
                    formatSummary));
                break;
            }

            default:
                warnings.Add($"File '{fi.Name}' uses an unsupported or encrypted vendor container format.");
                break;
        }

        var compatibleProfiles = new List<string>();
        foreach (var prof in _profileRepo.GetAllProfiles())
        {
            bool match = false;
            if (!string.IsNullOrWhiteSpace(fpHint) && fpHint.Contains(prof.Codename, StringComparison.OrdinalIgnoreCase))
                match = true;
            else if (!string.IsNullOrWhiteSpace(modelHint) &&
                     (modelHint.Contains(prof.Codename, StringComparison.OrdinalIgnoreCase) ||
                      prof.ModelCodes.Any(mc => modelHint.Contains(mc, StringComparison.OrdinalIgnoreCase))))
                match = true;
            else if (!string.IsNullOrWhiteSpace(chipsetHint) &&
                     string.Equals(chipsetHint, prof.ChipsetCode, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Chipset '{chipsetHint}' matches profile '{prof.ProfileId}', but exact board/project compatibility must still be verified before flashing.");
            }

            if (match)
            {
                compatibleProfiles.Add(prof.ProfileId);
            }
        }

        var result = new FirmwareInspectionResult
        {
            SourcePath = Path.GetFullPath(filePath),
            FileName = fi.Name,
            FileSizeBytes = fi.Length,
            PackageFormat = kind,
            IsFormatSupported = kind != FirmwareFormatKind.Unsupported,
            Sha256Hex = sha256,
            Crc32Hex = crc32,
            TargetChipsetHint = chipsetHint,
            TargetModelHint = modelHint,
            TargetBuildFingerprint = fpHint,
            ArchiveEntries = archiveEntries,
            Partitions = partitions,
            SparseInfo = sparseInfo,
            BootInfo = bootInfo,
            PayloadInfo = payloadInfo,
            PacInfo = pacInfo,
            ScatterInfo = scatterInfo,
            SuperInfo = superInfo,
            CompatibleProfileIds = compatibleProfiles,
            ParsingWarnings = warnings,
            MissingDependencies = missingDeps
        };

        _logger.LogInfo(
            "FirmwareExplorer",
            $"Inspected '{fi.Name}' -> Format={kind}, Partitions={partitions.Count}, Warnings={warnings.Count}");

        return result;
    }

    private static readonly System.Text.Json.JsonSerializerOptions PrettyBootJsonOptions = new() { WriteIndented = true };

    public async Task<string> ExtractArchiveEntryAsync(
        string packagePath,
        string entryName,
        string destinationDir,
        CancellationToken ct = default)
    {
        var format = DetectFormat(packagePath);
        if (format == FirmwareFormatKind.AndroidSuperImage ||
            (format == FirmwareFormatKind.AndroidSparseImage && IsSparseSuperImage(packagePath)))
        {
            var safePart = HashAndBinaryHelpers.SanitizeFileNameComponent(Path.GetFileNameWithoutExtension(entryName));
            if (HashAndBinaryHelpers.IsProtectedPartition(safePart))
            {
                throw new SafetyPolicyViolationException($"Extraction of protected partition '{safePart}' is blocked.");
            }
            Directory.CreateDirectory(destinationDir);
            var outImg = Path.Combine(destinationDir, $"{safePart}.img");
            return await ExtractSuperLogicalPartitionAsync(packagePath, safePart, outImg, ct).ConfigureAwait(false);
        }

        var output = await TarAndZipArchiveInspector.ExtractEntrySafeAsync(packagePath, format, entryName, destinationDir, ct).ConfigureAwait(false);
        _logger.LogInfo("ROMStudio", $"Extracted '{entryName}' -> '{output}'");
        return output;
    }

    private static bool IsSparseSuperImage(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            return AndroidSparseAndBootParsers.TryParseSuperLpMetadata(fs) is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> ExtractSuperLogicalPartitionAsync(
        string superImagePath,
        string logicalPartitionName,
        string outputImagePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(superImagePath))
        {
            throw new ServicingException("SUPER_FILE_NOT_FOUND", $"Super image does not exist: {superImagePath}");
        }

        var cleanName = logicalPartitionName.Trim();
        if (HashAndBinaryHelpers.IsProtectedPartition(cleanName))
        {
            throw new SafetyPolicyViolationException($"Extraction of protected partition '{cleanName}' is blocked.");
        }

        var fullOut = Path.GetFullPath(outputImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);

        var format = DetectFormat(superImagePath);
        string? tempUnsparsedPath = null;
        string rawSuperPath = superImagePath;

        try
        {
            if (format == FirmwareFormatKind.AndroidSparseImage)
            {
                tempUnsparsedPath = Path.Combine(Path.GetDirectoryName(fullOut)!, $".tmp_super_raw_{Guid.NewGuid():N}.img");
                await ConvertSparseImageToRawAsync(superImagePath, tempUnsparsedPath, ct).ConfigureAwait(false);
                rawSuperPath = tempUnsparsedPath;
            }

            long bytesWritten;
            await using (var superFs = new FileStream(rawSuperPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            await using (var outFs = new FileStream(fullOut, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
            {
                bytesWritten = await AndroidSparseAndBootParsers.ExtractSuperLogicalPartitionToStreamAsync(superFs, cleanName, outFs, ct).ConfigureAwait(false);
            }

            _logger.LogInfo("FirmwareExplorer", $"Extracted logical partition '{cleanName}' from '{Path.GetFileName(superImagePath)}' -> '{fullOut}' ({bytesWritten:N0} B)");
            return fullOut;
        }
        finally
        {
            if (tempUnsparsedPath is not null && File.Exists(tempUnsparsedPath))
            {
                try { File.Delete(tempUnsparsedPath); } catch { }
            }
        }
    }

    public async Task<BootImageUnpackReport> UnpackBootOrVendorBootImageAsync(
        string bootImagePath,
        string outputDirectory,
        CancellationToken ct = default)
    {
        if (!File.Exists(bootImagePath))
        {
            throw new ServicingException("BOOT_FILE_NOT_FOUND", $"Boot image does not exist: {bootImagePath}");
        }

        var fullDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullDir);

        await using var fs = new FileStream(bootImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var (header, kernelRange, ramdiskRange, dtbRange) = AndroidSparseAndBootParsers.ParseBootUnpackLayout(fs);

        string? kernelPath = null;
        string? ramdiskPath = null;
        string? dtbPath = null;

        if (kernelRange is { } kr && kr.Offset + kr.Size <= fs.Length)
        {
            kernelPath = Path.Combine(fullDir, "kernel");
            await SliceFileRegionAsync(fs, kr.Offset, kr.Size, kernelPath, ct).ConfigureAwait(false);
        }

        if (ramdiskRange is { } rr && rr.Offset + rr.Size <= fs.Length)
        {
            ramdiskPath = Path.Combine(fullDir, header.HeaderMagic == "VNDRBOOT" ? "vendor_ramdisk.cpio" : "ramdisk.cpio");
            await SliceFileRegionAsync(fs, rr.Offset, rr.Size, ramdiskPath, ct).ConfigureAwait(false);
        }

        if (dtbRange is { } dr && dr.Offset + dr.Size <= fs.Length)
        {
            dtbPath = Path.Combine(fullDir, "dtb");
            await SliceFileRegionAsync(fs, dr.Offset, dr.Size, dtbPath, ct).ConfigureAwait(false);
        }

        var metaPath = Path.Combine(fullDir, "boot_header_info.json");
        var metaPayload = new
        {
            SourceFile = Path.GetFileName(bootImagePath),
            header.HeaderMagic,
            header.HeaderVersion,
            header.PageSizeBytes,
            header.KernelSizeBytes,
            header.RamdiskSizeBytes,
            header.SecondOrDtbSizeBytes,
            header.DecodedOsVersion,
            header.DecodedSecurityPatch,
            header.CommandLine,
            ExtractedKernel = kernelPath,
            ExtractedRamdisk = ramdiskPath,
            ExtractedDtb = dtbPath
        };
        var json = System.Text.Json.JsonSerializer.Serialize(metaPayload, PrettyBootJsonOptions);
        await File.WriteAllTextAsync(metaPath, json, ct).ConfigureAwait(false);

        var summary = $"Unpacked {header.HeaderMagic} v{header.HeaderVersion} -> Kernel={(kernelPath is not null ? $"{header.KernelSizeBytes:N0} B" : "none")}, Ramdisk={(ramdiskPath is not null ? $"{header.RamdiskSizeBytes:N0} B" : "none")}, DTB={(dtbPath is not null ? $"{header.SecondOrDtbSizeBytes:N0} B" : "none")} in '{fullDir}'";
        _logger.LogInfo("FirmwareExplorer", summary);
        return new BootImageUnpackReport(
            Path.GetFullPath(bootImagePath),
            fullDir,
            header.HeaderMagic,
            header.HeaderVersion,
            header.PageSizeBytes,
            kernelPath,
            ramdiskPath,
            dtbPath,
            metaPath,
            summary);
    }

    private static async Task SliceFileRegionAsync(FileStream src, long offset, uint size, string outPath, CancellationToken ct)
    {
        src.Position = offset;
        await using var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        byte[] buf = new byte[65536];
        long rem = size;
        while (rem > 0)
        {
            int take = (int)Math.Min(buf.Length, rem);
            int got = await src.ReadAsync(buf.AsMemory(0, take), ct).ConfigureAwait(false);
            if (got <= 0) break;
            await dst.WriteAsync(buf.AsMemory(0, got), ct).ConfigureAwait(false);
            rem -= got;
        }
    }

    public async Task<string> ConvertSparseImageToRawAsync(
        string sparseImagePath,
        string outputRawImagePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(sparseImagePath))
        {
            throw new ServicingException("SPARSE_FILE_NOT_FOUND", $"Sparse image does not exist: {sparseImagePath}");
        }

        var fullOut = Path.GetFullPath(outputRawImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);

        long totalBytes;
        await using (var inFs = new FileStream(sparseImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        await using (var outFs = new FileStream(fullOut, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
        {
            totalBytes = await AndroidSparseAndBootParsers.ConvertSparseToRawAsync(inFs, outFs, ct).ConfigureAwait(false);
        }

        _logger.LogInfo("FirmwareExplorer", $"Unsparsed '{Path.GetFileName(sparseImagePath)}' -> '{fullOut}' ({totalBytes:N0} B)");
        return fullOut;
    }
}
