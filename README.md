# TranssionLab — Universal Android Servicing Suite

Professional offline-first Windows 10 / Windows 11 desktop application (`C#` / `.NET 8` / `WPF`) for diagnosing, identifying, inspecting firmware, and safely servicing supported **TECNO**, **Infinix**, and **itel** Android devices through **Android 16**.

> ### ⬇️ Download Single Windows Installer (`.exe`)
> **[Download `TranssionLab-Setup-v1.9.0-win-x64.exe`](https://github.com/laiss3r/TranssionLab/releases/download/v1.9.0/TranssionLab-Setup-v1.9.0-win-x64.exe)** *(Native 64-bit Microsoft C# Setup Wizard — scanned clean by Microsoft Defender • Self-contained `.NET 8` + WPF + bundled `adb.exe` & `fastboot.exe` + 125 Hz VCOM Catch-and-Hold Engine)*
>
> **First-Time Windows SmartScreen Tip (Unsigned Open-Source Build):**
> - In **Microsoft Edge Downloads**: click **`⋯`** next to the file → **Keep** → **Show more** → **Keep anyway**.
> - On the blue **"Windows protected your PC"** prompt: click **`More info`** → **`Run anyway`** *(or right-click the `.exe` → **Properties** → check **Unblock** → **OK**)*.
> - On Windows 11 if it says *"isn't a Microsoft-verified app"*: open **Settings → Apps → Advanced app settings → Choose where to get apps → Anywhere**.

---

## Key Capabilities

1. **Modular Protocol Subsystem (`TranssionLab.Protocols`)**
   - **USB & Windows PnP Discovery:** Real WMI `Win32_PnPEntity` enumeration, arrival/removal monitoring, COM-port mapping, VID/PID/MI classification (`0x2ECC` Transsion, `0x18D1` Google, `0x0E8D` MediaTek, `0x0BB4` MTK LK Fastboot, `0x1782` Unisoc, `0x05C6` Qualcomm), and Windows driver problem code diagnostics (`Code 10`, `22`, `28`, `43`, `52`).
   - **Standard Android (ADB & Recovery Sideload):** Distinguishes `Connected`, `Unauthorized`, `Authorizing`, `Offline`, `Recovery`, `Sideload`, and `Unavailable`; queries `getprop` identity, read-only battery/storage health, mode reboot transitions, and verified `.zip` OTA sideloading.
   - **Fastboot & Fastbootd:** Differentiates primary Bootloader Fastboot (`is-userspace: no`) from Userspace Fastbootd (`is-userspace: yes`), inspects bootloader variables, switches active A/B slots, and executes validated partition writes.
   - **MediaTek BROM / Preloader / DA:** Performs documented non-destructive BROM 4-byte sync (`A0 0A 50 05` -> `5F F5 AF FA`) and read-only `GET_HW_CODE (0xFD)`, `GET_HW_SW_VER (0xFC)`, and `GET_TARGET_CONFIG (0xD8)` to report SoC code and `SBC`/`SLA`/`DAA` hardware authentication requirements without exploits.
   - **MediaTek META Mode:** Dedicated META USB detector and external licensed `MetaCore.dll` readiness verifier—never fabricates META sessions on generic USB endpoints.
   - **Spreadtrum / Unisoc:** Implements HDLC `0x7E` framing, `0x7D` byte-stuffing, CRC-16 XMODEM verification, and non-destructive `BSL_REP_VER (0x81)` boot ROM version probing alongside binary `.pac` container inspection.
   - **DIAG Panel:** Classifies Qualcomm QCDM, Unisoc DIAG, and MediaTek ELT endpoints, preventing cross-chipset QCDM commands and blocking all protected NV/radio writes.

2. **Strict Engineering & Safety Guardrails**
   - **Explicit Multi-Device Selection:** Never silently selects a target when multiple phones are attached simultaneously.
   - **Provenance Tracking (`DETECTED` / `INFERRED` / `VERIFIED`):** Never infers an exact phone model from its chipset code alone.
   - **11-Step Validated Flash Manager:** Enforces device profile verification, Fastboot vs Fastbootd logical partition rules, sparse/raw capacity validation, NVRAM/calibration partition protection, mandatory Dry-Run validation, explicit confirmation phrase, post-flash state verification, and persistent JSON audit reports—never auto-erasing `userdata`.
   - **Bootloader Manager (Android 8–16):** Evaluates OEM unlock policy, discloses mandatory factory reset data loss, requires explicit confirmation phrase, and verifies resulting `unlocked` state via `fastboot getvar`.
   - **Dedicated Partition Manager (`PartitionManager` — Page #13):** Live GPT & Dynamic `super` partition table enumeration across Fastboot, Fastbootd, Authorized ADB, and Recovery; dynamic `super` logical sub-partition lifecycle (`create-logical-partition`, `resize-logical-partition`, `delete-logical-partition` in unlocked Fastbootd); controlled safe erase (`cache`, `metadata`, explicit `userdata` while permanently blocking FRP, NVRAM/calibration, and bootloaders); authorized Recovery/root ADB `.img` block readback with SHA-256 verification; and verified `AVB0` `vbmeta` policy flashing.

3. **Offline Firmware Explorer & ROM Studio (`TranssionLab.Firmware`)**
   - Native C# binary parsers & extractors for `.zip`, Signed OTA `.zip` (`payload.bin`), `.tar`, `.tar.gz`/`.tgz`, standalone Android `payload.bin` (`CrAU` v2 full-OTA `REPLACE`/`REPLACE_BZ`/`REPLACE_XZ`/`ZSTD`/`ZERO` partition extractor with SHA-256 verification), MediaTek Scatter (Text & XML), Unisoc `.pac` binary containers, Android Sparse images (`0xED26FF3A` inspection + Sparse-to-Raw `.img` converter), Android Dynamic `super.img` `LpMetadata` parser, Boot/Vendor_Boot images (`ANDROID!` v0–v4 & `VNDRBOOT`), `AVB0` vbmeta, and raw ext4/EROFS/F2FS images.
   - Separates stock partition extraction from AOSP/LineageOS custom ROM source build readiness validation.

4. **19-Page Dark Fluent WPF Interface (`TranssionLab.App`)**
   - Dashboard, Connection Manager, Device Explorer, ADB Manager, Fastboot Manager, MediaTek Tools, META Mode, SPD/Unisoc Tools, DIAG & Diagnostics, Bootloader Manager, Firmware Explorer, ROM Studio, **Partition Manager**, Flash Manager, Device Profiles (with in-app physical bench verification sign-off), Operation History, Live Logs, Settings, and Documentation & About.

---

## Quick Install on Windows 10 & Windows 11 (Single `.exe` Setup)

1. Download **`TranssionLab-Setup-v1.9.0-win-x64.exe`** and double-click it (no `.zip` extraction, external `.NET` runtime, or Administrator privileges required).
2. Complete the **First-Time Setup Wizard**:
   - Unpacks the full self-contained `.NET 8` + native WPF runtime directly into `%LOCALAPPDATA%\Programs\TranssionLab\` (avoiding `%TEMP%\.net` single-file native DLL extraction issues).
   - Pre-bundles official Apache-2.0 Google Android SDK Platform-Tools (`adb.exe` & `fastboot.exe`) in `platform-tools/` so ADB, Recovery Sideload, Fastboot, and Fastbootd work 100% offline immediately.
   - Includes a **33Hz MediaTek & Unisoc VCOM Catch-and-Hold Engine** + technician-friendly **1-Click `Read Phone Info` Hub** with docked live console and pinned step progress tracker so powered-off phones are caught and held in Preloader/BROM/Download mode before transitioning into battery charging.
   - Initializes `%LOCALAPPDATA%\TranssionLab\` (`logs`, `history`, `profiles`, `staging_images`, `partition_backups`, `exports`) and seeds all 8 reference TECNO / Infinix / itel device profiles.
   - Creates Desktop & Start Menu shortcuts + a clean Windows *Installed Apps* uninstaller (`Uninstall-TranssionLab.exe`).

---

## Building & Packaging from a Clean Checkout

```bash
dotnet restore TranssionLab.sln
dotnet build TranssionLab.sln --configuration Release -warnaserror
dotnet test TranssionLab.sln --configuration Release
bash scripts/package-release.sh 1.9.0
```

See `docs/` for:
- [`docs/FEATURE_AND_PROTOCOL_MATRIX.md`](docs/FEATURE_AND_PROTOCOL_MATRIX.md)
- [`docs/ADDING_VERIFIED_DEVICES.md`](docs/ADDING_VERIFIED_DEVICES.md)
- [`docs/THIRD_PARTY_LICENSES_AND_LEGAL.md`](docs/THIRD_PARTY_LICENSES_AND_LEGAL.md)
- [`docs/SECURITY_AND_COMPLIANCE_POLICY.md`](docs/SECURITY_AND_COMPLIANCE_POLICY.md)
