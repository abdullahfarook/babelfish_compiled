#!/usr/bin/env python3
"""Smoke test for MERGE ... OUTPUT (patches/0002-tsql-merge-output.patch).

Needs a running Babelfish with babelfishpg_tsql.enable_tsql_merge = on and pymssql:

    localdb create merge -s            # pg0-babelfish enables the setting by default
    pip install pymssql
    python3 merge_output_test.py [host:port] [user] [password]

Exits non-zero if any check fails.
"""
import sys
import pymssql

server = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1:1433"
user = sys.argv[2] if len(sys.argv) > 2 else "postgres"
password = sys.argv[3] if len(sys.argv) > 3 else "postgres"
cur = pymssql.connect(server, user, password, "master", autocommit=True).cursor()
failures = 0


def result_sets(sql):
    cur.execute(sql)
    out = []
    while True:
        try:
            out.append(cur.fetchall())
        except Exception:
            out.append(None)
        if not cur.nextset():
            return out


def check(name, got, want):
    global failures
    ok = got == want
    failures += not ok
    print(f"{'PASS' if ok else 'FAIL'}  {name}" + ("" if ok else f"\n        got  {got!r}\n        want {want!r}"))


def error_of(sql):
    try:
        result_sets(sql)
    except Exception as e:  # pymssql raises (number, message)
        return e.args[0]
    return None


cur.execute("IF OBJECT_ID('dbo.mo_t') IS NOT NULL DROP TABLE dbo.mo_t")
cur.execute("CREATE TABLE dbo.mo_t(id INT IDENTITY PRIMARY KEY, k NVARCHAR(20) UNIQUE, v INT)")
cur.execute("INSERT dbo.mo_t(k, v) VALUES (N'a', 1), (N'b', 2)")

# the statement Entity Framework Core sends for a multi-row insert with generated keys
check("EF Core shape: OUTPUT INSERTED.[id], i._Position",
      result_sets("MERGE [dbo].[mo_t] USING (VALUES (N'e1', 0), (N'e2', 1)) AS i ([k], _Position) ON 1=0 "
                  "WHEN NOT MATCHED THEN INSERT ([k]) VALUES (i.[k]) OUTPUT INSERTED.[id], i._Position;")[0],
      [(3, 0), (4, 1)])

check("upsert: $action, inserted.*, deleted.*",
      result_sets("MERGE dbo.mo_t AS t USING (VALUES (N'a', 10), (N'z', 99)) AS s(k, v) ON t.k = s.k "
                  "WHEN MATCHED THEN UPDATE SET v = s.v WHEN NOT MATCHED THEN INSERT(k, v) VALUES (s.k, s.v) "
                  "OUTPUT $action, inserted.k, deleted.v, inserted.v;")[0],
      [("UPDATE", "a", 1, 10), ("INSERT", "z", None, 99)])

check("DELETE action: deleted.*, $action",
      result_sets("MERGE dbo.mo_t AS t USING (VALUES (N'b')) AS s(k) ON t.k = s.k "
                  "WHEN MATCHED THEN DELETE OUTPUT deleted.k, $action;")[0],
      [("b", "DELETE")])

check("column alias and expressions",
      result_sets("MERGE dbo.mo_t AS t USING (VALUES (N'q', 5)) AS s(k, v) ON 1=0 "
                  "WHEN NOT MATCHED THEN INSERT(k, v) VALUES (s.k, s.v) "
                  "OUTPUT inserted.v * 2 AS doubled, ISNULL(deleted.v, -1) AS prev;")[0],
      [(10, -1)])

check("INSERTED.* / DELETED.*",
      result_sets("MERGE dbo.mo_t AS t USING (VALUES (N'a', 123)) AS s(k, v) ON t.k = s.k "
                  "WHEN MATCHED THEN UPDATE SET v = s.v OUTPUT deleted.*, inserted.*;")[0],
      [(1, "a", 10, 1, "a", 123)])

check("lowercase keywords, omitted INTO, compound SET",
      result_sets("merge dbo.mo_t using (values (N'a')) as s(k) on mo_t.k = s.k "
                  "when matched then update set v += 1 output Inserted.v, DELETED.v;")[0],
      [(124, 123)])

check("no matching rows -> empty result", result_sets(
    "MERGE dbo.mo_t AS t USING (VALUES (N'nope')) AS s(k) ON t.k = s.k WHEN MATCHED THEN UPDATE SET v = 1 OUTPUT inserted.id;")[0], [])

check("OUTPUT ... INTO is rejected (not silently ignored)",
      error_of("DECLARE @o TABLE(id INT); MERGE dbo.mo_t AS t USING (VALUES (N'x', 1)) AS s(k, v) ON 1=0 "
               "WHEN NOT MATCHED THEN INSERT(k, v) VALUES (s.k, s.v) OUTPUT inserted.id INTO @o;"),
      33557097)

# stored procedures re-parse the rewritten body, so this exercises the second ANTLR pass
cur.execute("IF OBJECT_ID('dbo.mo_upsert') IS NOT NULL DROP PROCEDURE dbo.mo_upsert")
cur.execute("CREATE PROCEDURE dbo.mo_upsert @k NVARCHAR(20), @v INT AS BEGIN "
            "MERGE dbo.mo_t AS t USING (SELECT @k, @v) AS s(k, v) ON t.k = s.k "
            "WHEN MATCHED THEN UPDATE SET v = s.v WHEN NOT MATCHED THEN INSERT(k, v) VALUES (s.k, s.v) "
            "OUTPUT $action, inserted.id, inserted.v; END")
check("inside a stored procedure: update", [(a, v) for a, _, v in result_sets("EXEC dbo.mo_upsert N'a', 77")[0]], [("UPDATE", 77)])
check("inside a stored procedure: insert", [(a, v) for a, _, v in result_sets("EXEC dbo.mo_upsert N'fresh', 5")[0]], [("INSERT", 5)])

cur.execute("BEGIN TRAN")
result_sets("MERGE dbo.mo_t AS t USING (VALUES (N'rb', 1)) AS s(k, v) ON t.k = s.k "
            "WHEN NOT MATCHED THEN INSERT(k, v) VALUES (s.k, s.v) OUTPUT inserted.id;")
cur.execute("ROLLBACK")
check("ROLLBACK undoes a MERGE ... OUTPUT", result_sets("SELECT COUNT(*) FROM dbo.mo_t WHERE k = N'rb'")[0], [(0,)])

check("duplicate key surfaces as error 2627",
      error_of("MERGE dbo.mo_t AS t USING (VALUES (N'dup', 1)) AS s(k, v) ON 1=0 "
               "WHEN NOT MATCHED THEN INSERT(k, v) VALUES (N'a', s.v) OUTPUT inserted.id;"), 2627)

values = ",".join(f"(N'p{i}', {i})" for i in range(300))
rows = result_sets(f"MERGE dbo.mo_t USING (VALUES {values}) AS i(k, _Position) ON 1=0 "
                   "WHEN NOT MATCHED THEN INSERT(k) VALUES (i.k) OUTPUT INSERTED.id, i._Position;")[0]
check("300-row batch: all rows returned, distinct ids, every position once",
      (len(rows), len({r[0] for r in rows}), sorted(r[1] for r in rows)), (300, 300, list(range(300))))

print("ALL PASSED" if not failures else f"{failures} CHECK(S) FAILED")
sys.exit(1 if failures else 0)
