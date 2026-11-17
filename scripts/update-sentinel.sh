#!/usr/bin/env bash
# Собирает встроенный в «Перенос» антивирус Sentinel: src/sentinel/sentinel.zip.
#
# Нужен .NET SDK 8 (Linux, macOS или Windows — сборка для win-x64 кросс-платформенная) и git.
# Использование:  scripts/update-sentinel.sh [ветка-или-коммит репозитория Antivirus-tool, по умолчанию main]
#
# В архиве: Sentinel.Cli.exe (автономный, .NET на компьютере не нужен), rules\, data\threat_hashes.bin,
# allowed_exclusions.json, THIRD_PARTY_NOTICES.md и sentinel-version.txt. MSBuild встраивает архив
# в Перенос.exe как ресурс Transfer.sentinel.zip; при первом запуске проверки он распаковывается
# в %LOCALAPPDATA%\Perenos\sentinel\.
set -euo pipefail
REF="${1:-main}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

git clone --quiet --depth 1 --branch "$REF" https://github.com/masterRaizer/Antivirus-tool "$WORK/src" 2>/dev/null \
  || { git clone --quiet https://github.com/masterRaizer/Antivirus-tool "$WORK/src" && git -C "$WORK/src" checkout --quiet "$REF"; }
COMMIT="$(git -C "$WORK/src" rev-parse --short HEAD)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$WORK/src/Directory.Build.props" | head -1)"

dotnet publish "$WORK/src/src/Sentinel.Cli/Sentinel.Cli.csproj" -c Release -r win-x64 --self-contained true --nologo \
  -o "$WORK/pub" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none

mkdir -p "$WORK/pkg"
cp "$WORK/pub/sentinel.exe" "$WORK/pkg/Sentinel.Cli.exe"
cp -r "$WORK/pub/rules" "$WORK/pub/data" "$WORK/pkg/"
cp "$WORK/src/allowed_exclusions.json" "$WORK/src/THIRD_PARTY_NOTICES.md" "$WORK/pkg/"
test -f "$WORK/pkg/data/threat_hashes.bin" || { echo "нет data/threat_hashes.bin" >&2; exit 1; }
test -n "$(ls "$WORK/pkg/rules")" || { echo "нет правил в rules/" >&2; exit 1; }
printf 'Sentinel %s (Antivirus-tool %s, %s)\r\n' "$VERSION" "$COMMIT" "$(date -u +%Y-%m-%d)" > "$WORK/pkg/sentinel-version.txt"

mkdir -p "$HERE/src/sentinel"
rm -f "$HERE/src/sentinel/sentinel.zip"
(cd "$WORK/pkg" && python3 - "$HERE/src/sentinel/sentinel.zip" <<'PY'
import os, sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in sorted(os.walk(".")):
        for f in sorted(files):
            path = os.path.relpath(os.path.join(root, f), ".")
            z.write(path, path.replace(os.sep, "/"))
PY
)
cat "$WORK/pkg/sentinel-version.txt"
ls -la "$HERE/src/sentinel/sentinel.zip"
