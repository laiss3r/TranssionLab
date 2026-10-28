#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

VERSION="${1:-1.9.0}"
RID="win-x64"
SETUP_NAME="TranssionLab-Setup-v${VERSION}-${RID}.exe"
BUNDLE_DIR="/tmp/transsionlab_bundle_${RID}"
BUNDLE_ZIP="/tmp/transsionlab_bundle_${RID}.zip"
SETUP_OUT_DIR="/tmp/transsionlab_setup_out"
PT_ZIP="/tmp/pt_win.zip"

export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1

echo "==> [1/7] Restoring solution dependencies..."
dotnet restore TranssionLab.sln -nodeReuse:false

echo "==> [2/7] Compiling solution with Roslyn static analysis (-warnaserror)..."
dotnet build TranssionLab.sln --configuration Release --no-restore -nodeReuse:false -p:UseSharedCompilation=false -m:1 -warnaserror

echo "==> [3/7] Verifying Windows ARM64 compilation target..."
dotnet build src/TranssionLab.App/TranssionLab.App.csproj --configuration Release -r win-arm64 -nodeReuse:false -p:UseSharedCompilation=false -m:1 -warnaserror

echo "==> [4/7] Running automated test suite..."
dotnet test tests/TranssionLab.Tests/TranssionLab.Tests.csproj --configuration Release --no-build -nodeReuse:false --filter "Category=SimulatedUnit"

echo "==> [5/7] Publishing multi-file self-contained Windows x64 runtime..."
rm -rf "$BUNDLE_DIR" "$BUNDLE_ZIP" "$SETUP_OUT_DIR"
dotnet publish src/TranssionLab.App/TranssionLab.App.csproj \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -nodeReuse:false \
  -p:UseSharedCompilation=false \
  -m:1 \
  -p:Version="$VERSION" \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  --output "$BUNDLE_DIR"

echo "==> [6/7] Bundling Apache-2.0 Android SDK Platform-Tools (adb.exe & fastboot.exe), profiles & docs..."
mkdir -p "$BUNDLE_DIR/platform-tools" "$BUNDLE_DIR/profiles" "$BUNDLE_DIR/docs" "$BUNDLE_DIR/tools" "$REPO_ROOT/releases"
if [ ! -f "$PT_ZIP" ]; then
  curl -fsSL -o "$PT_ZIP" "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
fi
python3 - "$PT_ZIP" "$BUNDLE_DIR/platform-tools" << 'PYEOF'
import os, sys, zipfile
zip_path, out_dir = sys.argv[1], sys.argv[2]
wanted = {"adb.exe", "fastboot.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "libwinpthread-1.dll", "NOTICE.txt", "source.properties"}
with zipfile.ZipFile(zip_path) as zf:
    for info in zf.infolist():
        base = os.path.basename(info.filename)
        if base in wanted:
            with zf.open(info) as src, open(os.path.join(out_dir, base), "wb") as dst:
                dst.write(src.read())
PYEOF

cp -r "$REPO_ROOT/profiles/"*.json "$BUNDLE_DIR/profiles/" 2>/dev/null || true
cp -r "$REPO_ROOT/docs/"*.md "$BUNDLE_DIR/docs/"
cp "$REPO_ROOT/README.md" "$BUNDLE_DIR/"

cat << 'EOF' > "$BUNDLE_DIR/tools/README-TOOLS.txt"
Official Apache-2.0 Android SDK Platform-Tools (adb.exe & fastboot.exe)
are pre-bundled in ../platform-tools/ (see ../platform-tools/NOTICE.txt).
EOF

python3 - "$BUNDLE_DIR" "$BUNDLE_ZIP" << 'PYEOF'
import os, sys, zipfile
src_dir, zip_out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(zip_out, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    for root, dirs, files in os.walk(src_dir):
        dirs.sort()
        files.sort()
        for f in files:
            full = os.path.join(root, f)
            rel = os.path.relpath(full, src_dir).replace("\\", "/")
            zf.write(full, rel)
PYEOF

echo "==> [7/7] Compiling native 64-bit C# .NET Framework 4.8 First-Time Setup Executable (${SETUP_NAME})..."
rm -f "$REPO_ROOT/releases/"*
dotnet build "$REPO_ROOT/installer/TranssionLab.Setup/TranssionLab.Setup.csproj" \
  --configuration Release \
  -nodeReuse:false \
  -p:UseSharedCompilation=false \
  -m:1 \
  -warnaserror \
  -p:Version="$VERSION" \
  -p:BundleZipPath="$BUNDLE_ZIP" \
  --output "$SETUP_OUT_DIR"

SETUP_PATH="$REPO_ROOT/releases/${SETUP_NAME}"
cp "$SETUP_OUT_DIR/TranssionLab-Setup.exe" "$SETUP_PATH"
rm -rf "$BUNDLE_DIR" "$BUNDLE_ZIP" "$SETUP_OUT_DIR"

echo "==> Standalone 64-bit Windows Setup executable built successfully (Single EXE only):"
ls -lh "$SETUP_PATH"
file "$SETUP_PATH"
sha256sum "$SETUP_PATH"
