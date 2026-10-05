using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

public class Doc { public int Id { get; set; } public string Title { get; set; } = "";
    [Column(TypeName = "vector(3)")] public SqlVector<float> Embedding { get; set; }
    [Column(TypeName = "vector(3)")] public SqlVector<float>? Optional { get; set; } }
public class VDb(string cs) : DbContext {
    public DbSet<Doc> Docs => Set<Doc>();
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseSqlServer(cs).LogTo(m => { if (m.Contains("VECTOR_DISTANCE") || m.Contains("INSERT") || m.Contains("MERGE")) Console.WriteLine("   SQL> " + m.Replace("\n", " ")[..Math.Min(260, m.Replace("\n", " ").Length)]); }, Microsoft.Extensions.Logging.LogLevel.Information);
}
public static class P {
    static int fail, pass;
    static async Task Step(string n, Func<Task> f) { try { await f(); pass++; Console.WriteLine("PASS  " + n); } catch (Exception e) { fail++; Console.WriteLine("FAIL  " + n + "\n      " + e.GetBaseException().Message.Split('\n')[0]); } }
    static SqlVector<float> V(params float[] x) => new(x);
    public static async Task<int> Main(string[] a) {
        var master = a[0]; var cs = new SqlConnectionStringBuilder(master) { InitialCatalog = "vecdb" }.ConnectionString;
        await using (var c = new SqlConnection(master)) { await c.OpenAsync(); await using var m = c.CreateCommand(); m.CommandText = "IF DB_ID('vecdb') IS NOT NULL DROP DATABASE vecdb; CREATE DATABASE vecdb"; await m.ExecuteNonQueryAsync(); }
        await Step("EnsureCreated with vector(3) columns", async () => { await using var db = new VDb(cs); if (!await db.Database.EnsureCreatedAsync()) throw new Exception("not created"); });
        await Step("Insert batch (SqlVector<float>, null optional)", async () => {
            await using var db = new VDb(cs);
            db.Docs.AddRange(new Doc { Title = "x-axis", Embedding = V(1, 0, 0) }, new Doc { Title = "y-axis", Embedding = V(0, 1, 0), Optional = V(9, 9, 9) },
                             new Doc { Title = "z-axis", Embedding = V(0, 0, 1) }, new Doc { Title = "diag", Embedding = V(1, 1, 0) });
            await db.SaveChangesAsync(); if (db.Docs.Local.Any(d => d.Id == 0)) throw new Exception("ids not generated");
        });
        await Step("Read back values + null", async () => {
            await using var db = new VDb(cs); var d = await db.Docs.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
            if (d.Count != 4 || !d[1].Embedding.Memory.ToArray().SequenceEqual(new float[] { 0, 1, 0 })) throw new Exception("values wrong");
            if (d[0].Optional != null || d[1].Optional is null || d[1].Optional.Value.Memory.ToArray()[0] != 9) throw new Exception("optional wrong");
        });
        await Step("KNN: EF.Functions.VectorDistance cosine (top 2)", async () => {
            await using var db = new VDb(cs); var q = V(1, 0.1f, 0);
            var r = await db.Docs.OrderBy(x => EF.Functions.VectorDistance("cosine", x.Embedding, q)).Take(2).Select(x => x.Title).ToListAsync();
            if (!r.SequenceEqual(new[] { "x-axis", "diag" })) throw new Exception("got " + string.Join(",", r));
        });
        await Step("KNN: euclidean + distance projection + filter", async () => {
            await using var db = new VDb(cs); var q = V(0, 0, 1);
            var r = await db.Docs.Where(x => EF.Functions.VectorDistance("euclidean", x.Embedding, q) < 1.5).Select(x => new { x.Title, D = EF.Functions.VectorDistance("euclidean", x.Embedding, q) }).OrderBy(x => x.D).ToListAsync();
            if (r.Count == 0 || r[0].Title != "z-axis" || r[0].D != 0) throw new Exception("got " + string.Join(",", r.Select(x => x.Title + ":" + x.D)));
        });
        await Step("Update vector (tracked) + ExecuteUpdate", async () => {
            await using var db = new VDb(cs); var d = await db.Docs.SingleAsync(x => x.Title == "z-axis"); d.Embedding = V(0, 0, 5); await db.SaveChangesAsync();
            await using var db2 = new VDb(cs); var back = await db2.Docs.SingleAsync(x => x.Title == "z-axis"); if (back.Embedding.Memory.ToArray()[2] != 5) throw new Exception("not updated");
            var nv = V(7, 7, 7); await db2.Docs.Where(x => x.Title == "diag").ExecuteUpdateAsync(s => s.SetProperty(x => x.Embedding, nv));
            await using var db3 = new VDb(cs); if ((await db3.Docs.SingleAsync(x => x.Title == "diag")).Embedding.Memory.ToArray()[0] != 7) throw new Exception("ExecuteUpdate not applied");
        });
        await Step("Wrong dimension rejected", async () => { await using var db = new VDb(cs); db.Docs.Add(new Doc { Title = "bad", Embedding = V(1, 2) }); try { await db.SaveChangesAsync(); throw new Exception("accepted"); } catch (DbUpdateException) { } });
        await Step("Delete", async () => { await using var db = new VDb(cs); await db.Docs.Where(x => x.Title == "diag").ExecuteDeleteAsync(); if (await db.Docs.CountAsync() != 3) throw new Exception("count"); });
        Console.WriteLine($"\n{pass} passed, {fail} failed"); return fail;
    }
}
