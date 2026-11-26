#!/usr/bin/env bash
# Crea i pacchetti Linux di CUS per un'architettura: tar.gz, .deb, .rpm e AppImage, tutti self-contained
# (non serve .NET sul PC di destinazione).
#
# Uso: packaging/linux/build-packages.sh <versione X.Y.Z> <linux-x64 | linux-arm64> <cartella di uscita>
#
# Richiede dotnet e python3, più gli strumenti indicati da queste variabili (release.yml li scarica con
# hash verificato): NFPM (eseguibile di nfpm), APPIMAGETOOL (appimagetool) e APPIMAGE_RUNTIME (il runtime
# AppImage dell'architettura di destinazione). CUS_SOURCE_DIR: sorgenti da pacchettizzare (di default il
# repository che contiene questo script).
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Uso: $0 <versione> <linux-x64|linux-arm64> <cartella di uscita>" >&2
  exit 2
fi
version=$1
rid=$2
out=$3
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Versione non valida (attesa X.Y.Z): $version" >&2
  exit 2
fi
case "$rid" in
  linux-x64) nfpm_arch=amd64; appimage_arch=x86_64 ;;
  linux-arm64) nfpm_arch=arm64; appimage_arch=aarch64 ;;
  *) echo "Architettura non supportata: $rid" >&2; exit 2 ;;
esac
: "${NFPM:?serve il percorso di nfpm}" "${APPIMAGETOOL:?serve il percorso di appimagetool}" "${APPIMAGE_RUNTIME:?serve il runtime AppImage}"

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "${CUS_SOURCE_DIR:-$here/../..}" && pwd)
mkdir -p "$out"
out=$(cd "$out" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

name="cus-$version-$rid"
app="$work/$name"
dotnet publish "$root/src/Cus.App.Linux/Cus.App.Linux.csproj" -c Release -r "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=none -o "$app"
test -x "$app/cus"
python3 "$here/extract-icon.py" "$root/src/Cus.UI/Assets/cus.ico" 256 "$work/cus.png"

# Archivio: la cartella dell'app, da estrarre dove si vuole.
tar -czf "$out/$name.tar.gz" -C "$work" "$name"

# .deb e .rpm (nfpm risolve i percorsi relativi del file di configurazione dalla cartella corrente).
export VERSION="$version" NFPM_ARCH="$nfpm_arch" PUBLISH_DIR="$app" ASSETS_DIR="$work"
(cd "$here" && "$NFPM" package --config nfpm.yaml --packager deb --target "$out/cus_${version}_${nfpm_arch}.deb")
(cd "$here" && "$NFPM" package --config nfpm.yaml --packager rpm --target "$out/cus-${version}-1.${appimage_arch}.rpm")

# AppImage: l'app in usr/lib/cus, avviata da AppRun. Runtime fissato invece di quello scaricato da
# appimagetool; senza FUSE (come sui runner) appimagetool si estrae ed esegue da sé.
appdir="$work/AppDir"
mkdir -p "$appdir/usr/lib"
cp -a "$app" "$appdir/usr/lib/cus"
install -m 0755 "$here/AppRun" "$appdir/AppRun"
install -m 0644 "$here/cus.desktop" "$appdir/cus.desktop"
install -m 0644 "$work/cus.png" "$appdir/cus.png"
ln -s cus.png "$appdir/.DirIcon"
ARCH="$appimage_arch" APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" --no-appstream \
  --runtime-file "$APPIMAGE_RUNTIME" "$appdir" "$out/cus-$version-$appimage_arch.AppImage"

ls -l "$out"
