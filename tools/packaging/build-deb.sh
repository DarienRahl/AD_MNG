#!/usr/bin/env bash
# Builds admng-watchdog_<version>-1_amd64.deb (one package for Debian 12 and 13) from packaging/admng-watchdog and
# framework-dependent linux-x64 publishes of src/AdMng.Watchdog.Service and src/AdMng.Watchdog.Keytab into one folder
# (docs/dev/watchdog-linux-design.md 4.4, 13.1, 13.3). --no-publish takes a folder that already holds both.
#
#   tools/packaging/build-deb.sh [--output DIR] [--version X.Y.Z] [--revision N] [--no-publish PUBLISH_DIR]
#
# Writes DIR/admng-watchdog_<version>-<revision>_amd64.deb and leaves the staged tree in DIR/stage/admng-watchdog
# (inspection, tests). Needs: dotnet SDK, dpkg-deb, gzip, strip (binutils).
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
src="$repo/packaging/admng-watchdog"
output="$repo/artifacts/deb"
version=""
revision=1
publish=""

while [ $# -gt 0 ]; do
    case "$1" in
        --output) output=$2; shift 2 ;;
        --version) version=$2; shift 2 ;;
        --revision) revision=$2; shift 2 ;;
        --no-publish) publish=$2; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ -z "$version" ]; then
    version=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo/Directory.Build.props")
fi
full_version="$version-$revision"
mkdir -p "$output"
output=$(cd "$output" && pwd)
stage="$output/stage/admng-watchdog"
rm -rf "$stage"
mkdir -p "$stage"

lib="$stage/usr/lib/admng-watchdog"
if [ -z "$publish" ]; then
    publish="$output/stage/publish"
    rm -rf "$publish"
    dotnet publish "$repo/src/AdMng.Watchdog.Service/AdMng.Watchdog.Service.csproj" -c Release -r linux-x64 \
        --self-contained false -p:DebugType=none -o "$publish" -nologo
    dotnet publish "$repo/src/AdMng.Watchdog.Keytab/AdMng.Watchdog.Keytab.csproj" -c Release -r linux-x64 \
        --self-contained false -p:DebugType=none -o "$publish" -nologo
fi
[ -f "$publish/admng-watchdog-keytab" ] || { echo "$publish lacks admng-watchdog-keytab (src/AdMng.Watchdog.Keytab)" >&2; exit 2; }
mkdir -p "$lib"
cp -a "$publish"/. "$lib"/
# Windows-only and development files do not ship in the Linux package.
rm -rf "$lib/Deploy" "$lib/appsettings.Development.json" "$lib"/*.pdb "$lib"/*.xml
install -m 0644 "$src/bin/admng-watchdog-functions" "$lib/admng-watchdog-functions"
# Native libraries ship stripped (lintian unstripped-binary-or-object); the apphost is left as published.
find "$lib" -name '*.so' -exec strip --strip-unneeded {} +

install -D -m 0755 "$src/bin/admng-watchdog" "$stage/usr/sbin/admng-watchdog"
# The keytab tool (4.4) is run by root: /usr/sbin/admng-watchdog-keytab → the apphost in /usr/lib/admng-watchdog.
ln -s ../lib/admng-watchdog/admng-watchdog-keytab "$stage/usr/sbin/admng-watchdog-keytab"
# B4 adds admng-wec-rotate.service/.timer together with its wec-rotate binary.
for unit in admng-watchdog.service admng-watchdog-failed.service admng-watchdog-keytab.service admng-watchdog-keytab.timer; do
    install -D -m 0644 "$src/systemd/$unit" "$stage/usr/lib/systemd/system/$unit"
done
install -D -m 0644 "$src/sysusers.d/admng-watchdog.conf" "$stage/usr/lib/sysusers.d/admng-watchdog.conf"
install -D -m 0644 "$src/tmpfiles.d/admng-watchdog.conf" "$stage/usr/lib/tmpfiles.d/admng-watchdog.conf"
install -D -m 0640 "$src/etc/appsettings.json" "$stage/etc/admng-watchdog/appsettings.json"
for conf in krb5.conf ldap.conf smb-client.conf; do
    install -D -m 0644 "$src/etc/$conf" "$stage/etc/admng-watchdog/$conf"
done
install -D -m 0644 "$src/nftables/admng-watchdog.nft" "$stage/usr/share/admng-watchdog/nftables/admng-watchdog.nft"

doc="$stage/usr/share/doc/admng-watchdog"
install -D -m 0644 "$src/debian/copyright" "$doc/copyright"
install -m 0644 "$src/debian/README.Debian" "$doc/README.Debian"
sed "s/@VERSION@/$full_version/" "$src/debian/changelog" | gzip -9n > "$doc/changelog.Debian.gz"
chmod 0644 "$doc/changelog.Debian.gz"

# Normalised modes: directories 0755, files 0644, executables 0755 (the apphost and the wrapper).
find "$stage" -type d -exec chmod 0755 {} +
find "$lib" -type f -exec chmod 0644 {} +
chmod 0755 "$lib/AdMng.Watchdog" "$lib/admng-watchdog-keytab"

control="$stage/DEBIAN"
mkdir -p "$control"
installed_size=$(du -sk --exclude=DEBIAN "$stage" | cut -f1)
sed -e "s/@VERSION@/$full_version/" -e "s/@INSTALLED_SIZE@/$installed_size/" "$src/debian/control.in" > "$control/control"
install -m 0644 "$src/debian/conffiles" "$src/debian/triggers" "$control/"
install -m 0755 "$src/debian/postinst" "$src/debian/prerm" "$src/debian/postrm" "$control/"
(cd "$stage" && find . \( -path ./DEBIAN -o -path ./etc \) -prune -o -type f -print0 | sort -z | xargs -0 md5sum | sed 's:  \./:  :' > DEBIAN/md5sums)
chmod 0644 "$control/md5sums"

deb="$output/admng-watchdog_${full_version}_amd64.deb"
SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$repo" log -1 --format=%ct 2>/dev/null || date +%s)} \
    dpkg-deb --root-owner-group -Zxz --build "$stage" "$deb"
echo "$deb"
