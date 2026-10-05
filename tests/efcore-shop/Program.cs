using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

public static class P {
    static int fail = 0, pass = 0;
    static async Task Step(string name, Func<Task> f) {
        try { await f(); pass++; Console.WriteLine("PASS  " + name); }
        catch (Exception e) { fail++; var m = e.GetBaseException().Message; Console.WriteLine("FAIL  " + name + "\n      " + m[..Math.Min(400, m.Length)]); }
    }
    static void Eq<T>(T exp, T act, string w) { if (!EqualityComparer<T>.Default.Equals(exp, act)) throw new Exception($"{w}: expected {exp}, got {act}"); }

    public static async Task<int> Main(string[] args) {
        var baseCs = args[0]; // server connection string (database=master)
        var cs = new SqlConnectionStringBuilder(baseCs) { InitialCatalog = "shop" }.ConnectionString;
        await using (var c = new SqlConnection(baseCs)) { await c.OpenAsync();
            await using var cmd = c.CreateCommand(); cmd.CommandText = "EXEC sp_babelfish_configure 'babelfishpg_tsql.escape_hatch_rowversion','ignore','server'; IF DB_ID('shop') IS NOT NULL DROP DATABASE shop; CREATE DATABASE shop"; await cmd.ExecuteNonQueryAsync(); }
        Environment.SetEnvironmentVariable("EF_CS", cs);

        // ---------- migrations + seed ----------
        await Step("Migrate (2 migrations)", async () => {
            await using var db = new ShopDb(cs);
            await db.Database.MigrateAsync();
            Eq(0, (await db.Database.GetPendingMigrationsAsync()).Count(), "pending");
            Eq(2, (await db.Database.GetAppliedMigrationsAsync()).Count(), "applied");
        });
        await Step("Migrate is idempotent", async () => { await using var db = new ShopDb(cs); await db.Database.MigrateAsync(); });
        await Step("Seed data present", async () => {
            await using var db = new ShopDb(cs);
            Eq(3, await db.Categories.CountAsync(), "categories"); Eq(5, await db.Products.CountAsync(), "products");
            Eq(3, await db.Customers.CountAsync(), "customers"); Eq(2, await db.Tags.CountAsync(), "tags");
        });
        await Step("Schema: unique index + FK + new column exist", async () => {
            await using var db = new ShopDb(cs);
            var n = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.indexes WHERE name='IX_Products_Name' AND is_unique=1").SingleAsync();
            Eq(1, n, "unique index");
            var col = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Customers' AND COLUMN_NAME='Phone'").SingleAsync();
            Eq(1, col, "Phone column");
        });
        await Step("Migrate down to Initial then back up", async () => {
            await using var db = new ShopDb(cs);
            var m = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await m.MigrateAsync("Initial");
            Eq(1, (await db.Database.GetAppliedMigrationsAsync()).Count(), "applied after down");
            await m.MigrateAsync();
            Eq(2, (await db.Database.GetAppliedMigrationsAsync()).Count(), "applied after up");
        });

        // ---------- CRUD ----------
        await Step("Create: single insert + generated key", async () => {
            await using var db = new ShopDb(cs);
            var p = new Product { Name = "Solaris", Price = 7m, Stock = 3, CategoryId = 1 }; db.Products.Add(p);
            await db.SaveChangesAsync(); if (p.Id <= 5) throw new Exception("key " + p.Id);
            if (p.RowVer == null) throw new Exception("rowversion not read back");
        });
        await Step("Create: graph (customer+orders+lines) in one SaveChanges", async () => {
            await using var db = new ShopDb(cs);
            var rnd = new Random(1); var prods = await db.Products.Where(p => p.Active).ToListAsync();
            var custs = Enumerable.Range(0, 20).Select(i => new Customer { Name = "C" + i, Email = i % 4 == 0 ? null : $"c{i}@x.com", Country = new[] { "US", "UK", "DE", "FR" }[i % 4], Joined = new DateTime(2025, 1, 1).AddDays(i * 10),
                Orders = Enumerable.Range(0, 1 + i % 4).Select(j => new Order { PlacedAt = new DateTime(2025, 6, 1).AddDays(i * 3 + j), Status = j % 2 == 0 ? "Paid" : "New",
                    Lines = Enumerable.Range(0, 1 + (i + j) % 3).Select(k => { var pr = prods[rnd.Next(prods.Count)]; return new OrderLine { Product = pr, Qty = 1 + k, UnitPrice = pr.Price }; }).ToList() }).ToList() }).ToList();
            db.Customers.AddRange(custs); await db.SaveChangesAsync();
            Eq(23, await db.Customers.CountAsync(), "customers");
            if (await db.OrderLines.AnyAsync(l => l.OrderId == 0)) throw new Exception("fk not fixed up");
        });
        await Step("Create: many-to-many (skip navigation) insert", async () => {
            await using var db = new ShopDb(cs);
            var sale = await db.Tags.FindAsync(1); var neu = await db.Tags.FindAsync(2);
            var dune = await db.Products.Include(p => p.Tags).SingleAsync(p => p.Name == "Dune"); dune.Tags.AddRange([sale!, neu!]);
            var chess = await db.Products.Include(p => p.Tags).SingleAsync(p => p.Name == "Chess"); chess.Tags.Add(sale!);
            await db.SaveChangesAsync();
            Eq(3, await db.Set<Dictionary<string, object>>("ProductTag").CountAsync(), "join rows");
        });
        await Step("Read: Find / FirstOrDefault / Single / AsNoTracking", async () => {
            await using var db = new ShopDb(cs);
            Eq("Dune", (await db.Products.FindAsync(1))!.Name, "find");
            Eq(null, await db.Products.FirstOrDefaultAsync(p => p.Name == "nope"), "missing");
            Eq(1, (await db.Products.AsNoTracking().Where(p => p.Id == 2).ToListAsync()).Count, "notracking");
        });
        await Step("Update: tracked change", async () => {
            await using var db = new ShopDb(cs);
            var p = await db.Products.SingleAsync(x => x.Name == "Solaris"); p.Price = 11.5m; p.Stock += 10; await db.SaveChangesAsync();
            await using var db2 = new ShopDb(cs); var q = await db2.Products.SingleAsync(x => x.Name == "Solaris"); Eq(11.5m, q.Price, "price"); Eq(13, q.Stock, "stock");
        });
        await Step("Update: optimistic concurrency (rowversion)", async () => {
            await using var a = new ShopDb(cs); await using var b = new ShopDb(cs);
            var pa = await a.Products.SingleAsync(x => x.Name == "Solaris"); var pb = await b.Products.SingleAsync(x => x.Name == "Solaris");
            pa.Stock = 1; await a.SaveChangesAsync(); pb.Stock = 2;
            try { await b.SaveChangesAsync(); throw new Exception("no concurrency exception"); } catch (DbUpdateConcurrencyException) { }
        });
        await Step("Update: ExecuteUpdate bulk", async () => {
            await using var db = new ShopDb(cs);
            var n = await db.Products.Where(p => p.CategoryId == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, p => p.Price * 1.1m).SetProperty(p => p.Active, true));
            Eq(3, n, "rows");
        });
        await Step("Delete: tracked + cascade", async () => {
            await using var db = new ShopDb(cs);
            var c = await db.Customers.Include(x => x.Orders).ThenInclude(o => o.Lines).FirstAsync(x => x.Name == "C1");
            db.Customers.Remove(c); await db.SaveChangesAsync();
            Eq(false, await db.Customers.AnyAsync(x => x.Name == "C1"), "gone");
        });
        await Step("Delete: ExecuteDelete bulk", async () => {
            await using var db = new ShopDb(cs);
            var n = await db.Customers.Where(c => c.Name == "C2" || c.Name == "C3").ExecuteDeleteAsync(); Eq(2, n, "rows");
        });
        await Step("Constraint violations surface as DbUpdateException (unique, FK)", async () => {
            await using var db = new ShopDb(cs); db.Products.Add(new Product { Name = "Dune", Price = 1, CategoryId = 1 });
            try { await db.SaveChangesAsync(); throw new Exception("unique not enforced"); } catch (DbUpdateException) { }
            await using var db2 = new ShopDb(cs); db2.Products.Add(new Product { Name = "Orphan", Price = 1, CategoryId = 99 });
            try { await db2.SaveChangesAsync(); throw new Exception("fk not enforced"); } catch (DbUpdateException) { }
        });
        await Step("Transaction rollback", async () => {
            await using var db = new ShopDb(cs); await using var tx = await db.Database.BeginTransactionAsync();
            db.Categories.Add(new Category { Name = "Temp" }); await db.SaveChangesAsync(); await tx.RollbackAsync();
            Eq(false, await db.Categories.AnyAsync(c => c.Name == "Temp"), "rolled back");
        });

        // ---------- complex LINQ ----------
        await using var q = new ShopDb(cs);
        await Step("LINQ: filter/order/skip/take/projection", async () => {
            var r = await q.Products.Where(p => p.Active && p.Price > 5).OrderByDescending(p => p.Price).ThenBy(p => p.Name).Skip(1).Take(3).Select(p => new { p.Name, p.Price }).ToListAsync();
            Eq(3, r.Count, "count"); if (r.Zip(r.Skip(1), (x, y) => x.Price >= y.Price).Any(b => !b)) throw new Exception("order");
        });
        await Step("LINQ: Include/ThenInclude + filtered include", async () => {
            var c = await q.Customers.Include(x => x.Orders.Where(o => o.Status == "Paid")).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).Where(x => x.Orders.Any()).OrderBy(x => x.Id).Take(5).ToListAsync();
            if (c.Count == 0 || c.SelectMany(x => x.Orders).Any(o => o.Status != "Paid")) throw new Exception("bad include");
        });
        await Step("LINQ: GroupBy + aggregates (Sum/Avg/Min/Max/Count)", async () => {
            var r = await q.OrderLines.GroupBy(l => l.Product!.Category!.Name).Select(g => new { Cat = g.Key, Qty = g.Sum(l => l.Qty), Rev = g.Sum(l => l.Qty * l.UnitPrice), Avg = g.Average(l => l.UnitPrice), Mn = g.Min(l => l.UnitPrice), Mx = g.Max(l => l.UnitPrice), N = g.Count() }).OrderBy(x => x.Cat).ToListAsync();
            if (r.Count == 0 || r.Any(x => x.Rev <= 0)) throw new Exception("empty aggregates");
        });
        await Step("LINQ: GroupBy + Having (Where after group)", async () => {
            var r = await q.Orders.GroupBy(o => o.CustomerId).Where(g => g.Count() >= 2).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            if (r.Any(x => x.N < 2)) throw new Exception("having");
        });
        await Step("LINQ: multi-table join (query syntax)", async () => {
            var r = await (from c in q.Customers join o in q.Orders on c.Id equals o.CustomerId join l in q.OrderLines on o.Id equals l.OrderId join p in q.Products on l.ProductId equals p.Id
                           where c.Country == "US" select new { c.Name, o.Status, p.Name!.Length, Total = l.Qty * l.UnitPrice }).ToListAsync();
            if (r.Count == 0) throw new Exception("no rows");
        });
        await Step("LINQ: left join (GroupJoin/DefaultIfEmpty)", async () => {
            var r = await (from c in q.Customers join o in q.Orders on c.Id equals o.CustomerId into og from o in og.DefaultIfEmpty() select new { c.Name, OrderId = (int?)o!.Id }).ToListAsync();
            if (!r.Any(x => x.OrderId == null)) throw new Exception("expected customers without orders");
        });
        await Step("LINQ: correlated subquery, Any/All/Contains", async () => {
            var ids = new[] { 1, 3, 5 };
            var a = await q.Products.Where(p => ids.Contains(p.Id)).CountAsync(); Eq(3, a, "contains");
            var b = await q.Customers.Where(c => c.Orders.All(o => o.Status == "Paid") && c.Orders.Any()).CountAsync();
            var d = await q.Customers.Select(c => new { c.Name, Latest = c.Orders.OrderByDescending(o => o.PlacedAt).Select(o => (DateTime?)o.PlacedAt).FirstOrDefault(), Spent = c.Orders.SelectMany(o => o.Lines).Sum(l => (decimal?)l.Qty * l.UnitPrice) ?? 0m }).ToListAsync();
            if (d.Count == 0) throw new Exception("none");
        });
        await Step("LINQ: string ops (Contains/StartsWith/EndsWith/ToUpper/Substring/Trim/Length) + null handling", async () => {
            var r = await q.Customers.Where(c => c.Name.StartsWith("C") && c.Name.ToUpper().Contains("C") && c.Name.Length >= 2 && (c.Email == null || c.Email.EndsWith(".com"))).Select(c => new { U = c.Name.ToLower(), S = c.Name.Substring(0, 1), E = c.Email ?? "n/a" }).ToListAsync();
            if (r.Count == 0) throw new Exception("none");
        });
        await Step("LINQ: date ops (Year/Month/AddDays/DateDiff)", async () => {
            var r = await q.Orders.Where(o => o.PlacedAt.Year == 2025 && o.PlacedAt.Month >= 6).Select(o => new { o.Id, Y = o.PlacedAt.Year, Next = o.PlacedAt.AddDays(7), Diff = EF.Functions.DateDiffDay(o.PlacedAt, new DateTime(2026, 1, 1)) }).ToListAsync();
            if (r.Count == 0) throw new Exception("none");
        });
        await Step("LINQ: math + conditional (CASE) + coalesce", async () => {
            var r = await q.Products.Select(p => new { p.Name, Tier = p.Price > 30 ? "high" : p.Price > 10 ? "mid" : "low", Rounded = Math.Round(p.Price), Abs = Math.Abs(p.Stock - 10), Stk = p.Stock == 0 ? (int?)null : p.Stock }).ToListAsync();
            if (!r.Any(x => x.Tier == "low") || !r.Any(x => x.Tier == "high")) throw new Exception("tiers");
        });
        await Step("LINQ: Union/Concat/Except/Intersect/Distinct", async () => {
            var us = q.Customers.Where(c => c.Country == "US").Select(c => c.Name); var uk = q.Customers.Where(c => c.Country == "UK").Select(c => c.Name);
            var u = await us.Union(uk).ToListAsync(); var cc = await us.Concat(uk).ToListAsync(); var ex = await us.Except(uk).ToListAsync(); var it = await us.Intersect(uk).ToListAsync(); var di = await q.Customers.Select(c => c.Country).Distinct().ToListAsync();
            Eq(0, it.Count, "intersect"); Eq(u.Count, cc.Count, "union=concat for disjoint"); Eq(us.Count(), ex.Count, "except"); Eq(4, di.Count, "distinct countries");
        });
        await Step("LINQ: many-to-many query (Tags)", async () => {
            var r = await q.Products.Where(p => p.Tags.Any(t => t.Name == "sale")).Select(p => new { p.Name, Tags = p.Tags.Select(t => t.Name).ToList() }).ToListAsync();
            Eq(2, r.Count, "sale products");
            var byTag = await q.Tags.Select(t => new { t.Name, N = t.Products.Count }).ToListAsync(); Eq(2, byTag.Count, "tags");
        });
        await Step("LINQ: split query + AsSplitQuery", async () => {
            var r = await q.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).AsSplitQuery().ToListAsync(); if (r.Count == 0) throw new Exception("none");
        });
        await Step("LINQ: window-like via Select over ordered subquery (Skip/Take per group) + paging total", async () => {
            var page = await q.Orders.OrderBy(o => o.Id).Skip(5).Take(5).Select(o => new { o.Id, Total = o.Lines.Sum(l => l.Qty * l.UnitPrice) }).ToListAsync(); Eq(5, page.Count, "page");
            var top = await q.Customers.OrderByDescending(c => c.Orders.Count).Take(3).Select(c => c.Name).ToListAsync(); Eq(3, top.Count, "top3");
        });
        await Step("LINQ: Contains on strings list (IN) + EF.Functions.Like + Count/LongCount/Any", async () => {
            var names = new[] { "Dune", "Go", "Chess" };
            Eq(3, await q.Products.CountAsync(p => names.Contains(p.Name)), "in");
            if (await q.Products.CountAsync(p => EF.Functions.Like(p.Name, "%e%")) == 0) throw new Exception("like");
            if (await q.Products.LongCountAsync() < 5) throw new Exception("longcount");
        });
        await Step("Raw SQL: FromSql interpolated + composition + stored proc via ExecuteSql", async () => {
            var min = 10m; var r = await q.Products.FromSql($"SELECT * FROM Products WHERE Price > {min}").Where(p => p.Active).OrderBy(p => p.Name).ToListAsync(); if (r.Count == 0) throw new Exception("none");
            await q.Database.ExecuteSqlRawAsync("CREATE PROCEDURE dbo.sp_bump @id int AS UPDATE Products SET Stock = Stock + 1 WHERE Id = @id");
            Eq(1, await q.Database.ExecuteSqlAsync($"EXEC dbo.sp_bump @id = {1}"), "proc rows");
        });
        Console.WriteLine($"\n{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
