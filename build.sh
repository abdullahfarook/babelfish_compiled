#!/bin/bash
# Build the Babelfish bundle. Self-contained: sources are cloned at the commits pinned in versions.env.
# Output: ./out/babelfish-<target>-pg18.tar.gz (+ .sha256).
#
#   ./build.sh [linux/amd64|linux/arm64]
#   FORK_DIR=~/src/fork EXT_DIR=~/src/ext ./build.sh     # use local checkouts instead of cloning
#
# Needs git, rsync and Docker (classic builder; no buildx).
set -euo pipefail
cd "$(dirname "$0")"
set -a; source versions.env; set +a
PLATFORM=${1:-linux/amd64}
TAG=babelfish-build:${PLATFORM//\//-}

# Stage one source tree into .src/<name>. Local dir if given, else a shallow clone of the pinned commit.
stage() { # name repo sha local_dir
  local name=$1 repo=$2 sha=$3 local_dir=${4:-} dest=.src/$1
  mkdir -p "$dest"
  if [ -n "$local_dir" ]; then
    echo ">> $name: using local checkout $local_dir"
    rsync -a --delete --exclude .git "$local_dir"/ "$dest"/
  elif [ -f "$dest/.pinned-sha" ] && [ "$(cat "$dest/.pinned-sha")" = "$sha" ]; then
    echo ">> $name: $sha already staged"
  else
    echo ">> $name: cloning $repo @ ${sha:0:12}"
    rm -rf "$dest" && mkdir -p "$dest"
    git -C "$dest" init -q
    git -C "$dest" fetch -q --depth 1 "$repo" "$sha"
    git -C "$dest" checkout -q FETCH_HEAD
    rm -rf "$dest/.git"
    echo "$sha" > "$dest/.pinned-sha"
  fi
}
stage fork "$BABELFISH_PG_REPO"  "$BABELFISH_PG_SHA"  "${FORK_DIR:-}"
stage ext  "$BABELFISH_EXT_REPO" "$BABELFISH_EXT_SHA" "${EXT_DIR:-}"

DOCKER_BUILDKIT=0 docker build --platform "$PLATFORM" -t "$TAG" -f Dockerfile \
  --build-arg PGVECTOR_VERSION="$PGVECTOR_VERSION" --build-arg PGBOUNCER_VERSION="$PGBOUNCER_VERSION" \
  --build-arg ANTLR4_VERSION="$ANTLR4_VERSION" --build-arg JOBS="${JOBS:-$(nproc)}" .
rm -rf out; mkdir -p out
cid=$(docker create --platform "$PLATFORM" "$TAG")
docker cp "$cid:/out/." out/
docker rm "$cid" >/dev/null
ls -la out
