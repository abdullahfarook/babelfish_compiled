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

`patches/*.patch` are applied to the extensions before they are built. They fix incompatibilities between
`babelfishpg_tds` and PostgreSQL 18 (currently: the TLS 1.2 cipher list used `ssl_tls13_ciphers`, so any
`ssl=on` server died at startup). `pg0` additionally sets `ssl_groups=prime256v1` because the extension cannot parse
PostgreSQL 18's default curve list. Both are candidates for upstream pull requests.

`patches/0002-tsql-merge-output.patch` adds `MERGE ... OUTPUT` (result-set form, as EF Core emits for batched inserts).
`patches/0003-tds-native-vector-type.patch` adds SQL Server 2025's native `vector` TDS type (0xF5) to `babelfishpg_tds`:
it parses the LOGIN7 feature extension and acknowledges VECTORSUPPORT, decodes vector RPC parameters and sends
`sys.vector` columns as binary, only to clients that negotiated it (everyone else keeps getting varchar).
Tests: `tests/merge_output_test.py`, `tests/vector_tsql_test.py`, `tests/sqlclient-vector/`, `tests/efcore-vector/`, `tests/efcore-shop/`.

After a release, copy the printed sha256 values into `pg0/versions.env`.
