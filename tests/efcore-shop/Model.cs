using Microsoft.EntityFrameworkCore;

public class Category { public int Id { get; set; } public string Name { get; set; } = ""; public List<Product> Products { get; set; } = new(); }
public class Tag { public int Id { get; set; } public string Name { get; set; } = ""; public List<Product> Products { get; set; } = new(); }
public class Product {
    public int Id { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; }
    public int Stock { get; set; } public bool Active { get; set; } = true;
    public int CategoryId { get; set; } public Category? Category { get; set; }
    public List<Tag> Tags { get; set; } = new(); public byte[]? RowVer { get; set; }
}
public class Customer { public int Id { get; set; } public string Name { get; set; } = ""; public string? Email { get; set; } public string Country { get; set; } = ""; public string? Phone { get; set; } public DateTime Joined { get; set; } public List<Order> Orders { get; set; } = new(); }
public class Order { public int Id { get; set; } public int CustomerId { get; set; } public Customer? Customer { get; set; } public DateTime PlacedAt { get; set; } public string Status { get; set; } = "New"; public List<OrderLine> Lines { get; set; } = new(); }
public class OrderLine { public int Id { get; set; } public int OrderId { get; set; } public Order? Order { get; set; } public int ProductId { get; set; } public Product? Product { get; set; } public int Qty { get; set; } public decimal UnitPrice { get; set; } }

public class ShopDb(string cs) : DbContext {
    public DbSet<Category> Categories => Set<Category>(); public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Product> Products => Set<Product>(); public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>(); public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public ShopDb() : this(Environment.GetEnvironmentVariable("EF_CS") ?? "Server=127.0.0.1,1433;Database=shop;User Id=postgres;Password=postgres;Encrypt=True;TrustServerCertificate=True") { }
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseSqlServer(cs);
    protected override void OnModelCreating(ModelBuilder mb) {
        mb.Entity<Product>(e => { e.Property(p => p.Price).HasPrecision(10, 2); e.Property(p => p.Name).HasMaxLength(100); e.HasIndex(p => p.Name).IsUnique(); e.Property(p => p.RowVer).IsRowVersion(); });
        mb.Entity<OrderLine>().Property(l => l.UnitPrice).HasPrecision(10, 2);
        mb.Entity<Customer>(e => { e.Property(c => c.Email).HasMaxLength(200); e.HasIndex(c => c.Email); });
        mb.Entity<Category>().HasData(new Category { Id = 1, Name = "Books" }, new Category { Id = 2, Name = "Games" }, new Category { Id = 3, Name = "Music" });
        mb.Entity<Tag>().HasData(new Tag { Id = 1, Name = "sale" }, new Tag { Id = 2, Name = "new" });
        mb.Entity<Product>().HasData(
            new Product { Id = 1, Name = "Dune", Price = 9.99m, Stock = 50, CategoryId = 1 },
            new Product { Id = 2, Name = "Neuromancer", Price = 8.50m, Stock = 20, CategoryId = 1 },
            new Product { Id = 3, Name = "Chess", Price = 25m, Stock = 5, CategoryId = 2 },
            new Product { Id = 4, Name = "Go", Price = 40m, Stock = 0, CategoryId = 2, Active = false },
            new Product { Id = 5, Name = "Kind of Blue", Price = 15m, Stock = 12, CategoryId = 3 });
        mb.Entity<Customer>().HasData(
            new Customer { Id = 1, Name = "Ann", Email = "ann@x.com", Country = "US", Joined = new DateTime(2024, 1, 5) },
            new Customer { Id = 2, Name = "Bob", Email = null, Country = "UK", Joined = new DateTime(2024, 3, 9) },
            new Customer { Id = 3, Name = "Cyd", Email = "cyd@x.com", Country = "US", Joined = new DateTime(2025, 2, 1) });
    }
}
