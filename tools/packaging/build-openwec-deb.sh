#!/usr/bin/env bash
# Builds admng-openwec_<version>-<revision>~deb<NN>_amd64.deb from the pinned OpenWEC commit (docs/dev/watchdog-linux-design.md
# 7.1, 13.2, 13.3) and, with --source-tarball, the GPL-3.0 corresponding source: the upstream tree of the commit, every
# crate it is built from (cargo vendor, upstream's Cargo.lock) and this recipe (packaging/admng-openwec + this script).
# Run it inside rust:slim-bookworm (--release 12) or rust:slim-trixie (--release 13): the binaries link the release's
# libssl/libkrb5/libsasl2, and the Depends line comes from dpkg-shlibdeps of that release.
#
#   tools/packaging/build-openwec-deb.sh --release 12|13 [--output DIR] [--revision N] [--source DIR] [--prebuilt DIR]
#                                        [--source-tarball] [--release-suffix SUFFIX]
#
#   --source DIR       an existing clone (checked out at the pinned commit) instead of a fresh clone
#   --prebuilt DIR     DIR/openwec and DIR/openwecd instead of cargo build (local packaging tests only)
#   --release-suffix   version suffix instead of ~deb<NN> (e.g. ~ubuntu24.04local for a host build)
#
# Needs: git, cargo (Rust ≥ 1.80), pkgconf, clang, make, libssl-dev, libkrb5-dev, libsasl2-dev, dpkg-dev, xz-utils.
set -euo pipefail

UPSTREAM_URL=https://github.com/cea-sec/openwec
UPSTREAM_COMMIT=86de07f8111a3b857504b6bb74098f65ff1b034d
UPSTREAM_VERSION=0.3.0+git20260908.86de07f

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
src="$repo/packaging/admng-openwec"
output="$repo/artifacts/deb"
revision=1
release=""
source_dir=""
prebuilt=""
tarball=false
suffix=""

while [ $# -gt 0 ]; do
    case "$1" in
        --release) release=$2; shift 2 ;;
        --output) output=$2; shift 2 ;;
        --revision) revision=$2; shift 2 ;;
        --source) source_dir=$2; shift 2 ;;
        --prebuilt) prebuilt=$2; shift 2 ;;
        --source-tarball) tarball=true; shift ;;
        --release-suffix) suffix=$2; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$release" in
    12|13) ;;
    *) echo "--release 12 or 13 is required" >&2; exit 2 ;;
esac
[ -n "$suffix" ] || suffix="~deb$release"
version="$UPSTREAM_VERSION-$revision$suffix"
mkdir -p "$output"
output=$(cd "$output" && pwd)
work="$output/stage/openwec-$release"
rm -rf "$work"
mkdir -p "$work"

if [ -z "$source_dir" ]; then
    source_dir="$work/src"
    git clone --quiet "$UPSTREAM_URL" "$source_dir"
    git -C "$source_dir" checkout --quiet "$UPSTREAM_COMMIT"
fi
head=$(git -C "$source_dir" rev-parse HEAD)
[ "$head" = "$UPSTREAM_COMMIT" ] || { echo "$source_dir is at $head, expected $UPSTREAM_COMMIT" >&2; exit 2; }
[ -z "$(git -C "$source_dir" status --porcelain --untracked-files=no)" ] || { echo "$source_dir has local changes" >&2; exit 2; }

if [ -n "$prebuilt" ]; then
    bin="$prebuilt"
else
    (cd "$source_dir" && cargo build --release --locked)
    bin="$source_dir/target/release"
fi
for b in openwec openwecd; do
    [ -x "$bin/$b" ] || { echo "$bin/$b is missing" >&2; exit 2; }
done

stage="$work/admng-openwec"
install -D -m 0755 "$bin/openwec" "$stage/usr/bin/openwec"
install -D -m 0755 "$bin/openwecd" "$stage/usr/bin/openwecd"
strip --strip-unneeded "$stage/usr/bin/openwec" "$stage/usr/bin/openwecd"
install -D -m 0644 "$src/systemd/openwecd.service" "$stage/usr/lib/systemd/system/openwecd.service"
install -D -m 0644 "$src/sysusers.d/admng-openwec.conf" "$stage/usr/lib/sysusers.d/admng-openwec.conf"
install -D -m 0644 "$src/tmpfiles.d/admng-openwec.conf" "$stage/usr/lib/tmpfiles.d/admng-openwec.conf"
install -D -m 0640 "$src/etc/openwec.conf.toml" "$stage/etc/openwec.conf.toml"
# The empty subscription directory: nothing below it is shipped (7.1, 13.2 – a shipped file would be a conffile whose
# .dpkg-dist copies OpenWEC would load).
install -d -m 0755 "$stage/etc/openwec/subscriptions"
install -D -m 0644 "$src/share/admng-dc-security.toml.example" "$stage/usr/share/admng-openwec/admng-dc-security.toml.example"
doc="$stage/usr/share/doc/admng-openwec"
mkdir -p "$doc"
sed -e "s/@UPSTREAM_COMMIT@/$UPSTREAM_COMMIT/g" -e "s/@VERSION_NOREV@/$UPSTREAM_VERSION/g" "$src/debian/copyright" > "$doc/copyright"
chmod 0644 "$doc/copyright"
install -m 0644 "$src/debian/README.Debian" "$doc/README.Debian"
sed -e "s/@VERSION@/$version/" -e "s/@UPSTREAM_COMMIT@/$UPSTREAM_COMMIT/" "$src/debian/changelog" | gzip -9n > "$doc/changelog.Debian.gz"
chmod 0644 "$doc/changelog.Debian.gz"
find "$stage" -type d -exec chmod 0755 {} +

# Depends from the release's shared libraries (libssl3 on Debian 12, libssl3t64 on 13).
if command -v dpkg-shlibdeps > /dev/null; then
    mkdir -p "$work/shlibs/debian"
    printf 'Source: admng-openwec\n\nPackage: admng-openwec\nArchitecture: amd64\n' > "$work/shlibs/debian/control"
    shlibs=$(cd "$work/shlibs" && dpkg-shlibdeps -O "$stage/usr/bin/openwec" "$stage/usr/bin/openwecd" 2>/dev/null | sed -n 's/^shlibs:Depends=//p')
fi
if [ -z "${shlibs:-}" ]; then
    if [ "$release" = 13 ]; then
        shlibs="libc6 (>= 2.34), libgcc-s1, libgssapi-krb5-2, libsasl2-2, libssl3t64, zlib1g"
    else
        shlibs="libc6 (>= 2.34), libgcc-s1, libgssapi-krb5-2, libsasl2-2, libssl3, zlib1g"
    fi
fi

control="$stage/DEBIAN"
mkdir -p "$control"
installed_size=$(du -sk --exclude=DEBIAN "$stage" | cut -f1)
sed -e "s/@VERSION@/$version/" -e "s/@INSTALLED_SIZE@/$installed_size/" -e "s/@SHLIBS_DEPENDS@/$shlibs/" \
    -e "s/@UPSTREAM_SHORT@/${UPSTREAM_COMMIT:0:7}/" "$src/debian/control.in" > "$control/control"
install -m 0644 "$src/debian/conffiles" "$control/"
install -m 0755 "$src/debian/postinst" "$src/debian/prerm" "$src/debian/postrm" "$control/"
(cd "$stage" && find . \( -path ./DEBIAN -o -path ./etc \) -prune -o -type f -print0 | sort -z | xargs -0 md5sum | sed 's:  \./:  :' > DEBIAN/md5sums)
chmod 0644 "$control/md5sums"

deb="$output/admng-openwec_${version}_amd64.deb"
SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$source_dir" log -1 --format=%ct)} \
    dpkg-deb --root-owner-group -Zxz --build "$stage" "$deb"
echo "$deb"

if $tarball; then
    # GPL-3.0 corresponding source (7.1): upstream tree + vendored crates + the recipe, one tarball per version.
    name="admng-openwec-$UPSTREAM_VERSION-source"
    tree="$work/$name"
    rm -rf "$tree"
    mkdir -p "$tree"
    git -C "$source_dir" archive --format=tar "$UPSTREAM_COMMIT" | tar -x -C "$tree"
    (cd "$tree" && cargo vendor --locked --versioned-dirs vendor > cargo-vendor-config.toml)
    mkdir -p "$tree/admng-recipe/packaging" "$tree/admng-recipe/tools/packaging"
    cp -a "$src" "$tree/admng-recipe/packaging/"
    cp -a "$repo/tools/packaging/build-openwec-deb.sh" "$tree/admng-recipe/tools/packaging/"
    printf '%s\n' "OpenWEC $UPSTREAM_URL commit $UPSTREAM_COMMIT, vendored crates (cargo vendor; use cargo-vendor-config.toml)" \
        "and the AdMng packaging recipe (admng-recipe/). Build: cargo build --release --locked --offline" > "$tree/SOURCE-OF-admng-openwec.txt"
    tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@${SOURCE_DATE_EPOCH:-$(git -C "$source_dir" log -1 --format=%ct)}" \
        -C "$work" -cJf "$output/$name.tar.xz" "$name"
    echo "$output/$name.tar.xz"
fi
