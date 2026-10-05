using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
var cs = args[0];
async Task Try(string name, Func<SqlConnection, Task> f) {
    try { await using var c = new SqlConnection(cs); await c.OpenAsync(); await f(c); Console.WriteLine("OK   " + name); }
    catch (Exception e) { Console.WriteLine("FAIL " + name + "\n     " + e.GetType().Name + ": " + e.Message.Split('\n')[0]); }
}
async Task Exec(SqlConnection c, string sql) { await using var m = c.CreateCommand(); m.CommandText = sql; await m.ExecuteNonQueryAsync(); }
await Try("setup", async c => { await Exec(c, "IF OBJECT_ID('sv') IS NOT NULL DROP TABLE sv"); await Exec(c, "CREATE TABLE sv (id int primary key, v vector(3))"); await Exec(c, "INSERT INTO sv VALUES (1,'[1,2,3]'),(2,'[4,5,6]')"); });
await Try("select vector column as string", async c => { await using var m = c.CreateCommand(); m.CommandText = "SELECT v FROM sv ORDER BY id"; await using var r = await m.ExecuteReaderAsync(); await r.ReadAsync(); Console.WriteLine("     type=" + r.GetFieldType(0) + " value=" + r.GetValue(0)); });
await Try("select vector column via GetFieldValue<SqlVector<float>>", async c => { await using var m = c.CreateCommand(); m.CommandText = "SELECT v FROM sv ORDER BY id"; await using var r = await m.ExecuteReaderAsync(); await r.ReadAsync(); var v = r.GetFieldValue<SqlVector<float>>(0); Console.WriteLine("     " + string.Join(",", v.Memory.ToArray())); });
await Try("insert with SqlVector<float> parameter", async c => { await using var m = c.CreateCommand(); m.CommandText = "INSERT INTO sv VALUES (3, @v)"; m.Parameters.Add(new SqlParameter("@v", new SqlVector<float>(new float[] { 7, 8, 9 }))); await m.ExecuteNonQueryAsync(); });
await Try("query with SqlVector<float> parameter", async c => { await using var m = c.CreateCommand(); m.CommandText = "SELECT TOP 1 id FROM sv ORDER BY VECTOR_DISTANCE('euclidean', v, @q)"; m.Parameters.Add(new SqlParameter("@q", new SqlVector<float>(new float[] { 4, 5, 6 }))); Console.WriteLine("     id=" + await m.ExecuteScalarAsync()); });
await Try("1536-dim vector (multi-packet) insert, read back, knn", async c => {
    await Exec(c, "IF OBJECT_ID('big') IS NOT NULL DROP TABLE big"); await Exec(c, "CREATE TABLE big (id int primary key, v vector(1536))");
    var rnd = new Random(7); float[][] rows = Enumerable.Range(0, 5).Select(_ => Enumerable.Range(0, 1536).Select(_ => (float)rnd.NextDouble()).ToArray()).ToArray();
    for (int i = 0; i < rows.Length; i++) { await using var m = c.CreateCommand(); m.CommandText = "INSERT INTO big VALUES (@i, @v)"; m.Parameters.AddWithValue("@i", i); m.Parameters.Add(new SqlParameter("@v", new SqlVector<float>(rows[i]))); await m.ExecuteNonQueryAsync(); }
    await using (var m = c.CreateCommand()) { m.CommandText = "SELECT id, v FROM big ORDER BY id"; await using var r = await m.ExecuteReaderAsync(); int n = 0; while (await r.ReadAsync()) { var v = r.GetFieldValue<SqlVector<float>>(1).Memory.ToArray(); if (!v.SequenceEqual(rows[r.GetInt32(0)])) throw new Exception("row mismatch " + r.GetInt32(0)); n++; } if (n != 5) throw new Exception("rows " + n); }
    await using (var m = c.CreateCommand()) { m.CommandText = "SELECT TOP 1 id FROM big ORDER BY VECTOR_DISTANCE('cosine', v, @q)"; m.Parameters.Add(new SqlParameter("@q", new SqlVector<float>(rows[3]))); var id = (int)(await m.ExecuteScalarAsync())!; if (id != 3) throw new Exception("nearest was " + id); }
});
await Try("null vector parameter + null column read", async c => {
    await Exec(c, "IF OBJECT_ID('nv') IS NOT NULL DROP TABLE nv"); await Exec(c, "CREATE TABLE nv (id int, v vector(3) NULL)");
    await using (var m = c.CreateCommand()) { m.CommandText = "INSERT INTO nv VALUES (1, @v)"; m.Parameters.Add(new SqlParameter("@v", SqlVector<float>.CreateNull(3))); await m.ExecuteNonQueryAsync(); }
    await using (var m = c.CreateCommand()) { m.CommandText = "SELECT v FROM nv"; await using var r = await m.ExecuteReaderAsync(); await r.ReadAsync(); if (!r.IsDBNull(0) && !r.GetFieldValue<SqlVector<float>>(0).IsNull) throw new Exception("not null"); }
});
