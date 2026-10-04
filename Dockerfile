# Builds the Babelfish PostgreSQL fork + extensions + ANTLR4 runtime + pgvector + pgbouncer
# on Ubuntu 22.04 (glibc 2.35) so the result runs on every distro pg0 supports.
#
# Built by build.sh (classic builder). build.sh stages the sources into .src/{fork,ext}.
FROM ubuntu:22.04 AS build
ENV DEBIAN_FRONTEND=noninteractive
ARG ANTLR4_VERSION=4.13.2
ARG PGVECTOR_VERSION
ARG PGBOUNCER_VERSION
ARG JOBS=8

RUN apt-get update && apt-get install -y --no-install-recommends \
      build-essential flex bison gawk gettext pkg-config cmake lld patchelf \
      libxml2-dev libxml2-utils libxslt1-dev libssl-dev libreadline-dev zlib1g-dev \
      libldap2-dev libpam0g-dev libossp-uuid-dev uuid-dev libicu-dev icu-devtools \
      libkrb5-dev libutfcpp-dev unixodbc-dev libevent-dev libc-ares-dev \
      xsltproc file curl ca-certificates unzip openjdk-17-jre-headless

ENV PREFIX=/opt/babelfish PG_CONFIG=/opt/babelfish/bin/pg_config

# --- ANTLR4 C++ runtime ----------------------------------------------------
COPY .src/ext/contrib/babelfishpg_tsql/antlr/thirdparty/antlr/antlr-${ANTLR4_VERSION}-complete.jar /usr/local/lib/
RUN curl -fsSL -o /tmp/antlr.zip https://www.antlr.org/download/antlr4-cpp-runtime-${ANTLR4_VERSION}-source.zip \
 && mkdir /tmp/antlr && unzip -q -d /tmp/antlr /tmp/antlr.zip && cd /tmp/antlr && mkdir build && cd build \
 && cmake .. -DANTLR_JAR_LOCATION=/usr/local/lib/antlr-${ANTLR4_VERSION}-complete.jar \
      -DCMAKE_INSTALL_PREFIX=/usr/local -DCMAKE_BUILD_TYPE=Release -DWITH_DEMO=False \
 && make -j${JOBS} && make install && rm -rf /tmp/antlr /tmp/antlr.zip

# --- Babelfish PostgreSQL fork --------------------------------------------
COPY .src/fork/ /src/pg
WORKDIR /src/pg
RUN ./configure --prefix=${PREFIX} --with-ldap --with-libxml --with-pam --with-uuid=ossp \
      --enable-nls --with-libxslt --with-icu --with-openssl \
 && make -j${JOBS} && make install \
 && make -C contrib -j${JOBS} && make -C contrib install

# --- Babelfish extensions --------------------------------------------------
COPY .src/ext/ /src/ext
ENV ANTLR4_JAVA_BIN=/usr/bin/java ANTLR_EXECUTABLE=/usr/local/lib/antlr-${ANTLR4_VERSION}-complete.jar \
    ANTLR4_RUNTIME_LIBRARIES=/usr/local/include/antlr4-runtime
RUN test -x /usr/bin/java || { echo "java missing"; exit 1; }; cp /usr/local/lib/libantlr4-runtime.so.${ANTLR4_VERSION} ${PREFIX}/lib/ \
 && cd /src/ext/contrib/babelfishpg_tsql/antlr && cmake -Wno-dev . && make all
# The extensions include headers and grammar files straight from the fork's source tree.
ENV PG_SRC=/src/pg
# babelfishpg_tds still guards OpenSSL >=1.1 APIs behind macros that older PostgreSQL's configure
# defined and PG18's pg_config.h no longer does. OpenSSL 3 (Ubuntu 22.04) always has them.
ENV COPT="-DHAVE_OPENSSL_INIT_SSL=1 -DHAVE_BIO_METH_NEW=1"
RUN set -e; cd /src/ext/contrib; for e in babelfishpg_common babelfishpg_money babelfishpg_tds babelfishpg_tsql; do \
      make -C $e -j${JOBS} PG_CONFIG=${PG_CONFIG} || exit 1; make -C $e install PG_CONFIG=${PG_CONFIG} || exit 1; done

# --- pgvector + pgbouncer --------------------------------------------------
RUN curl -fsSL https://github.com/pgvector/pgvector/archive/refs/tags/v${PGVECTOR_VERSION}.tar.gz | tar xz -C /src \
 && make -C /src/pgvector-${PGVECTOR_VERSION} -j${JOBS} PG_CONFIG=${PG_CONFIG} \
 && make -C /src/pgvector-${PGVECTOR_VERSION} install PG_CONFIG=${PG_CONFIG}
RUN curl -fsSL https://www.pgbouncer.org/downloads/files/${PGBOUNCER_VERSION}/pgbouncer-${PGBOUNCER_VERSION}.tar.gz | tar xz -C /src \
 && cd /src/pgbouncer-${PGBOUNCER_VERSION} && ./configure --prefix=${PREFIX} --with-openssl --with-cares \
 && make -j${JOBS} && make install

# --- Relocatable bundle ----------------------------------------------------
COPY bundle.sh /usr/local/bin/bundle.sh
ARG PGVECTOR_VERSION
RUN PGVECTOR_VERSION=${PGVECTOR_VERSION} PGBOUNCER_VERSION=${PGBOUNCER_VERSION} bash /usr/local/bin/bundle.sh ${PREFIX} /out

