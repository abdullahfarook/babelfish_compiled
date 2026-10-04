# babelfish_compiled

Builds the relocatable Babelfish bundle consumed by `pg0 --features babelfish`: the Babelfish PostgreSQL
fork, `babelfishpg_{common,money,tds,tsql}`, ANTLR4 runtime, pgvector and pgbouncer, built against that fork.

```bash
./build.sh                 # linux/amd64 -> out/babelfish-x86_64-unknown-linux-gnu-pg18.tar.gz (+ .sha256)
./build.sh linux/arm64     # QEMU smoke test only; release builds use a native arm64 runner
```

Self-contained: `build.sh` clones the fork and extensions at the commits pinned in `versions.env` into `.src/`.
To build from local checkouts instead: `FORK_DIR=... EXT_DIR=... ./build.sh`.
Needs git, rsync and Docker (classic builder; no buildx). Runs on Ubuntu 22.04 so binaries need at most glibc 2.35.

- `bundle.sh` makes the install relocatable: `$ORIGIN` RUNPATHs, non-glibc shared libs copied into `lib/`,
  hard links dereferenced, one top-level directory (pg0 strips it on extract).
- Modules are in `lib/postgresql/`, extension files in `share/postgresql/extension/`.
- `babelfishpg_tds` is compiled with `-DHAVE_OPENSSL_INIT_SSL=1 -DHAVE_BIO_METH_NEW=1` (see Dockerfile): the
  sources still guard OpenSSL >= 1.1 APIs behind macros PostgreSQL 18's `pg_config.h` no longer defines.

After a release, copy the printed sha256 values into `pg0/versions.env`.
