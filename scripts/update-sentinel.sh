#!/usr/bin/env bash
# Обновляет встроенный в «Перенос» антивирус Sentinel: src/sentinel/sentinel.zip(.partN).
#
# Использование:
#   scripts/update-sentinel.sh                  # последний релиз Antivirus-tool (рекомендуется)
#   scripts/update-sentinel.sh 1.3.11           # конкретный релиз
#   scripts/update-sentinel.sh --source main    # собрать из исходников (нужен .NET SDK 8; база угроз — только стартовая из репозитория)
#
# Основной путь — официальный релиз: в нём Sentinel.Cli.exe собран вместе с большой базой хешей угроз
# (ESET, signature-base, Talos, Unit 42, Sophos, VirusShare, MalwareBazaar), которую из исходников не собрать без ключей.
# Из переносной версии релиза (Sentinel-vX-portable-win-x64.zip) берутся только консольный сканер и его данные:
# Sentinel.Cli.exe, rules\, data\, allowed_exclusions.json, THIRD_PARTY_NOTICES.md; добавляется sentinel-version.txt.
#
# GitHub не принимает файлы больше 100 МБ, поэтому архив больше 90 МБ режется на части sentinel.zip.part1, .part2, …
# MSBuild встраивает все части в Перенос.exe; программа склеивает их и распаковывает в %LOCALAPPDATA%\Perenos\sentinel\.
set -euo pipefail
REPO="masterRaizer/Antivirus-tool"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$HERE/src/sentinel"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/pkg"

if [ "${1:-}" = "--source" ]; then
  REF="${2:-main}"
  git clone --quiet https://github.com/$REPO "$WORK/src"
  git -C "$WORK/src" checkout --quiet "$REF"
  COMMIT="$(git -C "$WORK/src" rev-parse --short HEAD)"
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$WORK/src/Directory.Build.props" | head -1)"
  dotnet publish "$WORK/src/src/Sentinel.Cli/Sentinel.Cli.csproj" -c Release -r win-x64 --self-contained true --nologo \
    -o "$WORK/pub" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none
  cp "$WORK/pub/sentinel.exe" "$WORK/pkg/Sentinel.Cli.exe"
  cp -r "$WORK/pub/rules" "$WORK/pub/data" "$WORK/pkg/"
  cp "$WORK/src/allowed_exclusions.json" "$WORK/src/THIRD_PARTY_NOTICES.md" "$WORK/pkg/"
  ORIGIN="сборка из исходников $COMMIT, стартовая база угроз"
else
  VERSION="${1:-}"
  if [ -z "$VERSION" ]; then
    VERSION="$(git ls-remote --tags https://github.com/$REPO 'v*' | sed -n 's:.*refs/tags/v\([0-9.]*\)$:\1:p' | sort -V | tail -1)"
  fi
  VERSION="${VERSION#v}"
  URL="https://github.com/$REPO/releases/download/v$VERSION/Sentinel-v$VERSION-portable-win-x64.zip"
  echo "Скачиваю $URL"
  curl -fsSL --retry 3 -o "$WORK/portable.zip" "$URL" || { echo "Релиз v$VERSION ещё не опубликован (нет $URL)" >&2; exit 1; }
  python3 -I - "$WORK/portable.zip" "$WORK/pkg" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
keep = ("Sentinel.Cli.exe", "allowed_exclusions.json", "THIRD_PARTY_NOTICES.md")
with zipfile.ZipFile(src) as z:
    for i in z.infolist():
        name = i.filename.replace("\\", "/")
        if name.endswith("/") or ".." in name.split("/"):
            continue
        if name in keep or name.startswith("rules/") or name.startswith("data/"):
            path = os.path.join(dst, *name.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with z.open(i) as r, open(path, "wb") as w:
                w.write(r.read())
PY
  ORIGIN="релиз v$VERSION"
fi

test -f "$WORK/pkg/Sentinel.Cli.exe" || { echo "нет Sentinel.Cli.exe" >&2; exit 1; }
test -n "$(ls "$WORK/pkg/rules" 2>/dev/null)" || { echo "нет правил в rules/" >&2; exit 1; }
test -f "$WORK/pkg/data/threat_hashes.bin" || { echo "нет data/threat_hashes.bin" >&2; exit 1; }
printf 'Sentinel %s (%s, %s)\r\n' "$VERSION" "$ORIGIN" "$(date -u +%Y-%m-%d)" > "$WORK/pkg/sentinel-version.txt"

mkdir -p "$OUT"
rm -f "$OUT"/sentinel.zip "$OUT"/sentinel.zip.part*
python3 -I - "$WORK/pkg" "$OUT" <<'PY'
import os, sys, zipfile
pkg, out = sys.argv[1], sys.argv[2]
zpath = os.path.join(out, "sentinel.zip")
with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in sorted(os.walk(pkg)):
        for f in sorted(files):
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, pkg).replace(os.sep, "/"))
limit = 90 * 1024 * 1024
size = os.path.getsize(zpath)
if size > limit:
    with open(zpath, "rb") as r:
        n = 0
        while True:
            chunk = r.read(limit)
            if not chunk:
                break
            n += 1
            with open(zpath + ".part%d" % n, "wb") as w:
                w.write(chunk)
    os.remove(zpath)
    print("архив %.1f МБ разрезан на %d части по 90 МБ" % (size / 1048576.0, n))
else:
    print("архив %.1f МБ" % (size / 1048576.0))
PY
cat "$WORK/pkg/sentinel-version.txt"
ls -la "$OUT"
