#!/bin/bash
# Local build (classic docker builder; no buildx needed).
# Output: ./out/babelfish-<target>-pg18.tar.gz (+ .sha256).
# Usage: ./build.sh [linux/amd64|linux/arm64]
set -euo pipefail
cd "$(dirname "$0")"
set -a; source versions.env; set +a
PLATFORM=${1:-linux/amd64}
TAG=babelfish-build:${PLATFORM//\//-}
# Context is the parent dir holding postgresql_modified_for_babelfish/ and babelfish_extensions/.
DOCKER_BUILDKIT=0 docker build --platform "$PLATFORM" -t "$TAG" -f Dockerfile \
  --build-arg PGVECTOR_VERSION="$PGVECTOR_VERSION" --build-arg PGBOUNCER_VERSION="$PGBOUNCER_VERSION" \
  --build-arg ANTLR4_VERSION="$ANTLR4_VERSION" --build-arg JOBS="${JOBS:-$(nproc)}" ..
rm -rf out; mkdir -p out
cid=$(docker create --platform "$PLATFORM" "$TAG")
docker cp "$cid:/out/." out/
docker rm "$cid" >/dev/null
ls -la out
