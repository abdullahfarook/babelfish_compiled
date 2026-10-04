#!/bin/bash
# Make $1 (install prefix) relocatable and tar it into $2.
# Non-glibc shared libs that differ across distros are copied into lib/ and
# every ELF gets an $ORIGIN-relative RUNPATH. glibc/libstdc++ stay system-provided.
set -euo pipefail
PREFIX=$1; OUT=$2
ARCH=$(uname -m)-unknown-linux-gnu
TOP=babelfish-$ARCH   # top-level dir, like the theseus tarballs (pg0 strips it on extract)
STAGE=$OUT/$TOP; mkdir -p "$STAGE"
cp -a "$PREFIX"/. "$STAGE"/
cd "$STAGE"
rm -rf include share/doc share/man
# Libs we must never bundle (provided by the OS / glibc).
SKIP='linux-vdso|ld-linux|libc\.so|libm\.so|libpthread|libdl|librt|libresolv|libutil|libnsl|libgcc_s|libstdc\+\+'
elfs() { find bin lib -type f \( -perm -u+x -o -name '*.so*' \) -exec sh -c 'file -b "$1" | grep -q ELF' _ {} \; -print; }
for pass in 1 2; do
  for f in $(elfs); do
    { ldd "$f" 2>/dev/null || true; } | awk '/=> \//{print $3}' | { grep -Ev "$SKIP" || true; } | while read -r dep; do
      case "$dep" in "$PREFIX"/*|"$STAGE"/*) continue;; esac
      b=$(basename "$dep"); [ -e "lib/$b" ] || cp -L "$dep" "lib/$b"
    done
  done
done
for f in $(elfs); do
  case "$f" in bin/*) rp='$ORIGIN/../lib';; lib/postgresql/*) rp='$ORIGIN/..';; *) rp='$ORIGIN';; esac
  patchelf --set-rpath "$rp" "$f" 2>/dev/null || true
  strip --strip-unneeded "$f" 2>/dev/null || true
done
cat > BUNDLE_MANIFEST <<M
target=$ARCH
postgres=$(bin/postgres --version)
pgvector=${PGVECTOR_VERSION:-}
pgbouncer=${PGBOUNCER_VERSION:-}
M
tar --hard-dereference -C "$OUT" -czf "$OUT/babelfish-$ARCH-pg18.tar.gz" "$TOP"
(cd "$OUT" && sha256sum "babelfish-$ARCH-pg18.tar.gz" > "babelfish-$ARCH-pg18.tar.gz.sha256")
rm -rf "$STAGE"
