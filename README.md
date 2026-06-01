# LiteSql

> A lightweight LINQ to SQL replacement for .NET Core, powered by Dapper.

[![.NET Standard 2.0](https://img.shields.io/badge/.NET%20Standard-2.0-blue)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Why LiteSql?

**LINQ to SQL** (`System.Data.Linq`) is a .NET Framework-only ORM with no .NET Core support. Migrating large codebases away from it is painful and risky.

**LiteSql** bridges the gap by providing a **drop-in compatible API** backed by [Dapper](https://github.com/DapperLib/Dapper) — so your existing L2S patterns keep working on .NET Core with minimal code changes.

### Key Features

- 🔗 **FK Navigation** — Auto-load related entities via `[Association]` attributes
- 🎯 **Include()** — Selective FK loading per-query, avoiding unnecessary queries
- 🔄 **L2S-compatible API** — `GetTable<T>()`, `InsertOnSubmit()`, `DeleteOnSubmit()`, `SubmitChanges()`
- ⚡ **Full Async API** — `SubmitChangesAsync()`, `WhereAsync()`, `FirstOrDefaultAsync()`, `SingleAsync()`, `FindAsync()`
- 🔍 **Server-side filtering** — `Where(predicate)` translates LINQ expressions to SQL WHERE
- 📊 **Sorting & Pagination** — `OrderBy()`, `ThenBy()`, `Skip()`, `Take()` — server-side SQL
- 🧲 **Find by PK** — `Find()` / `FindAsync()` for efficient primary key lookups
- 🚀 **AsNoTracking** — Skip change tracking for read-only queries
- 📊 **Dirty Update** — Only changed columns are UPDATEd, reducing data transfer
- 🎯 **Same mapping attributes** — `[Table]`, `[Column]` with identical signatures
- 🔌 **Multi-database** — SQL Server, SQLite, MySQL, PostgreSQL (dialect-aware quoting, pagination, UPSERT)
- 📦 **.NET Standard 2.0** — Works on both .NET Framework and .NET Core / .NET 5+
- 🛠️ **Code Generator** — Generate entities from SQL Server database or `.dbml` files
- 🧪 **204 tests** — Unit, integration & performance tests with SQLite in-memory
- 🔑 **CodeGen Keyword Escaping** — Auto-escapes C# reserved keywords (`From` → `@From`)

## Packages

| Package | Type | Description |
|---|---|---|
| **LiteSql** | Library (.NET Standard 2.0) | Core ORM — add to your project |
| **LiteSql.CodeGen** | dotnet global tool (.NET 8+) | Code generator — generate entity classes |

---

## Getting Started

### 1. Install the Code Generator

```bash
# Install from local NuGet source
dotnet tool install --global LiteSql.CodeGen --add-source path/to/LiteSql/nupkg
```

### 2. Generate Entity Classes

**From SQL Server (recommended for .NET Core projects):**

```bash
litesql-codegen -c "Server=.;Database=RAFInventory;Trusted_Connection=true;TrustServerCertificate=true" -n RAF.Models -o Models/dbRAF.cs
```

**From existing DBML file (migration from L2S):**

```bash
litesql-codegen DataClasses1.dbml -n MyNamespace -o Models/DataClasses1.cs
```

**All options:**

```
litesql-codegen <input.dbml> [options]          # from DBML file
litesql-codegen -c <connection-string> [options] # from a live database

Options:
  -o, --output <path>       Output .cs file path (or directory with --split)
  -n, --namespace <ns>      Target namespace (default: Models)
  -c, --connection <cs>     Database connection string
  -p, --provider <name>     DB provider: sqlserver (default), mysql, postgresql, sqlite
      --context <name>      Override context class name
      --split               One file per entity
      --single-file         All entities in one file (default)
  -h, --help                Show help
```

### 3. Add LiteSql to Your Project

```bash
dotnet add package LiteSql --source path/to/LiteSql/nupkg
```

### 4. Configure at Startup

```csharp
using LiteSql;
using Microsoft.Data.SqlClient;

// Set connection factory (once at app startup)
LiteContext.ConnectionFactory = cs => new SqlConnection(cs);

// Set default connection string for generated context
RAFInventoryDataContext.DefaultConnectionString = "Server=.;Database=RAFInventory;...";
```

### 5. Use It

```csharp
using (var db = RAFInventoryDataContext.New())
{
    // Server-side query (LINQ → SQL WHERE)
    var activeUsers = db.tbSYS_Users.Where(u => u.Status).ToList();

    // Find by primary key
    var user = db.tbSYS_Users.Find(userId);

    // Insert
    var newUser = new tbSYS_User { Username = "alice", FullName = "Alice" };
    db.tbSYS_Users.InsertOnSubmit(newUser);

    // Modify loaded entity (auto-detected by update tracking)
    user.Department = "Engineering";

    // Delete
    var old = db.tbSYS_Users.FirstOrDefault(u => u.Username == "bob");
    if (old != null) db.tbSYS_Users.DeleteOnSubmit(old);

    // Commit all changes in a single transaction
    db.SubmitChanges();
}
```

---

## Async API

All sync methods have async counterparts:

```csharp
using (var db = RAFInventoryDataContext.New())
{
    var users  = await db.tbSYS_Users.WhereAsync(u => u.Status);
    var user   = await db.tbSYS_Users.FirstOrDefaultAsync(u => u.Username == "admin");
    var first  = await db.tbSYS_Users.FirstAsync(u => u.Status);
    var single = await db.tbSYS_Users.SingleAsync(u => u.Username == "admin");
    var maybe  = await db.tbSYS_Users.SingleOrDefaultAsync(u => u.Username == "admin");
    var count  = await db.tbSYS_Users.CountAsync(u => u.Status);
    var exists = await db.tbSYS_Users.AnyAsync(u => u.Username == "admin");
    var all    = await db.tbSYS_Users.ToListAsync();
    var found  = await db.tbSYS_Users.FindAsync(userId);

    // Async submit
    db.tbSYS_Users.InsertOnSubmit(newUser);
    await db.SubmitChangesAsync();

    // Async raw SQL
    var results  = await db.ExecuteQueryAsync<tbSYS_User>("SELECT * FROM tbSYS_User WHERE Status = {0}", true);
    var affected = await db.ExecuteCommandAsync("UPDATE tbSYS_User SET Status = {0} WHERE id = {1}", false, id);
}
```

## Server-Side Filtering

LINQ expressions are translated to SQL, not filtered in memory:

```csharp
db.Products.Where(p => p.Price > 100 && p.IsActive);
// → SELECT * FROM [Products] WHERE [Price] > @w0 AND [IsActive] = @w1

db.Products.Where(p => p.Name.Contains("Widget"));
// → SELECT * FROM [Products] WHERE [Name] LIKE '%Widget%'

var ids = new List<int> { 1, 2, 3 };
db.Products.Where(p => ids.Contains(p.CategoryId));
// → SELECT * FROM [Products] WHERE [CategoryId] IN (@w0, @w1, @w2)

db.Products.FirstOrDefault(p => p.Id == 1);
// → SELECT TOP 1 * FROM [Products] WHERE [Id] = @w0

db.Products.Single(p => p.Code == "ABC");
// → SELECT * FROM [Products] WHERE [Code] = @w0 (throws if 0 or >1 results)

db.Products.SingleOrDefault(p => p.Code == "ABC");
// → SELECT * FROM [Products] WHERE [Code] = @w0 (returns null if 0, throws if >1)
```

## Sorting & Pagination

Fluent API for server-side sorting and pagination:

```csharp
// Single column sort
var items = db.Products.OrderBy(p => p.Name).ToList();
// → SELECT * FROM [Products] ORDER BY [Name] ASC

// Multi-column sort
var items = db.Products
    .OrderByDescending(p => p.CreatedDate)
    .ThenBy(p => p.Name)
    .Where(p => p.IsActive);
// → SELECT * FROM [Products] WHERE [IsActive] = @w0 ORDER BY [CreatedDate] DESC, [Name] ASC

// Pagination
var page2 = db.Products
    .OrderBy(p => p.Id)
    .Skip(10).Take(5)
    .ToList();
// SQLite:      ... ORDER BY [Id] ASC LIMIT 5 OFFSET 10
// SQL Server:  ... ORDER BY [Id] ASC OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY

// Async variants
var items = await db.Products.OrderBy(p => p.Name).WhereAsync(p => p.IsActive);
var first = await db.Products.OrderByDescending(p => p.Date).FirstOrDefaultAsync(p => p.IsActive);
var page  = await db.Products.OrderBy(p => p.Id).Skip(20).Take(10).ToListAsync();

// With Include
var orders = db.Orders
    .Include(o => o.Customer)
    .OrderByDescending(o => o.OrderDate)
    .Skip(0).Take(20)
    .Where(o => o.Status == "Active");
```

## Select Projection

Project specific columns at the SQL level (avoids `SELECT *`):

```csharp
// DTO projection
var dtos = db.Products
    .Select(p => new ProductDto { Id = p.Id, Name = p.Name },
            p => p.IsActive);
// → SELECT [id] AS [Id], [Name] AS [Name] FROM [Products] WHERE [IsActive] = @w0

// With OrderBy + Pagination
var page = db.Products
    .OrderBy(p => p.Name)
    .Skip(10).Take(5)
    .Select(p => new ProductDto { Id = p.Id, Name = p.Name });

// Async
var dtos = await db.Products.SelectAsync(p => new { p.Id, p.Name });
```

## GroupBy

Server-side aggregation with `GroupBy()` + `Select()`. Works with anonymous types and DTOs:

```csharp
// Single-column group with aggregates
var summary = db.Orders
    .GroupBy(o => o.CustomerId)
    .Select(g => new {
        CustomerId = g.Key,
        OrderCount = g.Count(),
        Total      = g.Sum(x => x.Amount),
        Avg        = g.Average(x => x.Amount)
    })
    .ToList();
// → SELECT [CustomerId], COUNT(*) AS [OrderCount], SUM([Amount]) AS [Total], AVG([Amount]) AS [Avg]
//   FROM [Orders] GROUP BY [CustomerId]

// Multi-column group
var byStatusYear = db.Orders
    .GroupBy(o => new { o.Status, o.Year })
    .Select(g => new { g.Key.Status, g.Key.Year, Count = g.Count() });

// HAVING (filter on aggregates)
var heavy = db.Orders
    .GroupBy(o => o.CustomerId)
    .Where(g => g.Count() > 1 && g.Sum(x => x.Amount) > 400)
    .Select(g => new { g.Key, Total = g.Sum(x => x.Amount) });

// Conditional count + expression aggregate
var stats = db.Orders
    .GroupBy(o => o.CustomerId)
    .Select(g => new {
        g.Key,
        CompletedCount = g.Count(x => x.Status == "Completed"),
        Weighted       = g.Sum(x => x.Amount * x.Qty)
    });

// WHERE before GroupBy (filters rows before grouping)
var completedOnly = db.Orders
    .AndWhere(o => o.Status == "Completed")
    .GroupBy(o => o.CustomerId)
    .Select(g => new { g.Key, Total = g.Sum(x => x.Amount) });

// Async
var top = await db.Orders
    .GroupBy(o => o.CustomerId)
    .SelectAsync(g => new { g.Key, Total = g.Sum(x => x.Amount) });
```

## Join

Two-table `Join` / `LeftJoin` with a result projection, optional WHERE, and async:

```csharp
var rows = db.GetTable<Order>()
    .Join(db.GetTable<Customer>(),
        o => o.CustomerId,
        c => c.Id,
        (o, c) => new { o.Id, Customer = c.Name, o.Amount })
    .Where((o, c) => c.Country == "VN" && o.Amount > 100)
    .ToList();

// Left join + async
var left = await db.GetTable<Order>()
    .LeftJoin(db.GetTable<OrderDetail>(),
        o => o.Id,
        d => d.OrderId,
        (o, d) => new OrderLineDto { OrderId = o.Id, Sku = d.Sku })
    .ToListAsync();
```

### Multi-table joins (3+ tables)

Use `JoinMany` / `LeftJoinMany` to start a chain, then add tables. Lambda parameters are
positional — the j-th parameter is the j-th joined table:

```csharp
var rows = db.GetTable<Order>()
    .JoinMany(db.GetTable<Customer>(), (o, c) => o.CustomerId == c.Id)
    .Join(db.GetTable<Country>(),      (o, c, ct) => c.CountryId == ct.Id)
    .Join(db.GetTable<OrderLine>(),    (o, c, ct, l) => l.OrderId == o.Id)
    .Where((o, c, ct, l) => ct.Name == "Vietnam" && l.Qty >= 2)
    .Select((o, c, ct, l) => new { o.Id, Customer = c.Name, Country = ct.Name, l.Sku, l.Qty });

// LEFT JOIN steps, DTO projection, and async are all supported:
var dto = await db.GetTable<Order>()
    .JoinMany(db.GetTable<Customer>(), (o, c) => o.CustomerId == c.Id)
    .LeftJoin(db.GetTable<OrderLine>(), (o, c, l) => l.OrderId == o.Id)
    .SelectAsync((o, c, l) => new OrderReportDto { OrderId = o.Id, Customer = c.Name, Sku = l.Sku });
```

Supports up to 4 tables out of the box (chain types `JoinChain<T1,T2>` … `JoinChain<T1,T2,T3,T4>`).
For 5+ tables or subqueries-in-SELECT, use `ExecuteQuery<T>()` / `FromSql<T>()`.

## Conditional Filtering (WhereIf / AndWhere)

Build queries dynamically; filters are accumulated and combined (AND) at the terminal call:

```csharp
var results = db.Orders
    .WhereIf(!string.IsNullOrEmpty(status), o => o.Status == status)
    .WhereIf(minAmount.HasValue, o => o.Amount >= minAmount.Value)
    .AndWhere(o => o.IsActive)
    .OrderBy(o => o.Date)
    .ToList();
// Only conditions whose flag is true are merged into one SQL WHERE.
```

## Subqueries (EXISTS / IN)

Correlated subqueries without raw SQL:

```csharp
// EXISTS — customers that have at least one order
var withOrders = db.Customers
    .WhereExists(db.Orders, (c, o) => o.CustomerId == c.Id)
    .ToList();
// → SELECT * FROM [Customers] WHERE (EXISTS (SELECT 1 FROM [Orders] AS sq0 WHERE sq0.[CustomerId] = [Customers].[Id]))

// NOT EXISTS — customers with no orders
var noOrders = db.Customers
    .WhereNotExists(db.Orders, (c, o) => o.CustomerId == c.Id);

// IN subquery (with optional inner filter)
var activeCustomers = db.Customers
    .WhereIn(c => c.Id, db.Orders, o => o.CustomerId, o => o.Status == "Active");

// NOT IN
var inactive = db.Customers.WhereNotIn(c => c.Id, db.Orders, o => o.CustomerId);

// Subqueries combine with regular predicates
var result = db.Customers
    .AndWhere(c => c.IsActive)
    .WhereExists(db.Orders, (c, o) => o.CustomerId == c.Id)
    .ToList();
```

## Graph Insert (parent + children)

Insert a parent and its one-to-many child collections in one transaction; the generated parent
key is propagated to each child's FK (recursive for nested graphs):

```csharp
var order = new Order {
    Code = "ORD-1",
    Lines = {
        new OrderLine { Sku = "A", Qty = 2 },
        new OrderLine { Sku = "B", Qty = 5 }
    }
};
db.InsertGraph(order);          // or: await db.InsertGraphAsync(order)
// order.Id is set; each line.OrderId is set to order.Id; all inserted in one transaction.
```

The child collection is declared with an `[Association]` where `ThisKey` is the parent PK and
`OtherKey` is the child FK:

```csharp
[Association(ThisKey = "Id", OtherKey = "OrderId")]
public List<OrderLine> Lines { get; set; } = new();
```

## Schema Migrations

Code-based migrations with up/down, history tracking, and a dialect-aware `SchemaBuilder`:

```csharp
public class CreateUsers : Migration
{
    public override string Id => "0001_CreateUsers";

    public override void Up(SchemaBuilder s) => s
        .CreateTable("Users", t => {
            t.Int("Id").Identity().PrimaryKey();
            t.String("Username", 100).NotNull().Unique();
            t.String("FullName", 200);
        })
        .CreateIndex("Users", "IX_Users_Username", unique: true, "Username");

    public override void Down(SchemaBuilder s) => s
        .DropIndex("Users", "IX_Users_Username")
        .DropTable("Users");
}

// Apply (idempotent — applied migrations are tracked in __LiteSqlMigrations and skipped on re-run)
var runner = new MigrationRunner(connection, new SqlServerDialect());
runner.MigrateUp(new Migration[] { new CreateUsers() });

// Rollback + inspect history
runner.MigrateDown(new CreateUsers());
var applied = runner.GetAppliedIds();
```

`SchemaBuilder` supports `CreateTable`/`DropTable`, `AddColumn`/`DropColumn`,
`CreateIndex`/`DropIndex`, and raw `Sql()`. Each migration runs in its own transaction.

## Optimistic Concurrency

Mark a column `[Column(IsVersion = true)]`. Updates add the loaded version to the WHERE and bump
integral versions; a no-op update (row changed elsewhere) throws `ConcurrencyException`:

```csharp
[Column(Name = "RowVersion", IsVersion = true)]
public int RowVersion { get; set; }

try { db.SubmitChanges(); }
catch (ConcurrencyException) { /* reload + retry */ }
```

## Value Converters (read + write)

```csharp
db.Converters.Add<OrderStatus, string>(
    toDb:   v => v.ToString(),
    fromDb: v => (OrderStatus)Enum.Parse(typeof(OrderStatus), v));
// Applied automatically on INSERT/UPDATE and on SELECT (via Dapper type handler).
```

## FK Navigation (Auto-Load)

LiteSql automatically loads FK related entities via `[Association]` attributes.
Uses **batch IN queries** to avoid N+1 — 10 entities with 2 FKs = only 3 queries total.

```csharp
using (var db = RAFInventoryDataContext.New())
{
    // Default: ALL FK navigation properties auto-loaded
    var stockIn = db.tbINV_StockIns.FirstOrDefault(s => s.id == 3);
    Console.WriteLine(stockIn.tbINV_Warehouse.Code);       // ✅ loaded
    Console.WriteLine(stockIn.tbSYS_User_Leader.FullName); // ✅ loaded
}
```

### Include() — Selective FK Loading

For **better performance**, specify only the FKs you need:

```csharp
using (var db = RAFInventoryDataContext.New())
{
    // Only load Warehouse FK (1 extra query instead of N)
    var stockIn = db.tbINV_StockIns
        .Include(s => s.tbINV_Warehouse)
        .FirstOrDefault(s => s.id == 3);
    Console.WriteLine(stockIn.tbINV_Warehouse.Code);       // ✅ loaded
    Console.WriteLine(stockIn.tbSYS_User_Leader?.FullName); // null (not included)

    // Multiple Include
    var items = db.tbINV_StockIns
        .Include(s => s.tbINV_Warehouse)
        .Include(s => s.tbSYS_User_Leader)
        .Where(s => s.Status == true);
}
```

### DataLoadOptions (L2S-compatible)

```csharp
var options = new DataLoadOptions();
options.LoadWith<tbINV_StockIn>(s => s.tbINV_Warehouse);
db.LoadOptions = options; // All queries on this context use these rules

// Empty LoadOptions = disable auto-load (fastest for list queries)
db.LoadOptions = new DataLoadOptions();
```

## Read-Only Queries (AsNoTracking)

```csharp
// Skip change tracking for better performance
var items = db.GetTable<Product>().AsNoTracking()
    .Where(p => p.IsActive).ToList();
// Modifications to these entities will NOT be saved on SubmitChanges
```

## Raw SQL (L2S-style positional parameters)

```csharp
using (var db = new LiteContext(connection))
{
    // {0}, {1} parameters — just like L2S
    var results = db.ExecuteQuery<Product>(
        "SELECT * FROM Products WHERE CategoryId = {0} AND Price > {1}",
        categoryId, minPrice);

    int affected = db.ExecuteCommand(
        "UPDATE Products SET Price = {0} WHERE Id = {1}",
        newPrice, productId);
}
```

## Transaction Support

```csharp
using (var db = new LiteContext(connection))
{
    db.Connection.Open();
    db.Transaction = db.Connection.BeginTransaction();
    try
    {
        db.GetTable<Order>().InsertOnSubmit(order);
        db.GetTable<OrderDetail>().InsertAllOnSubmit(details);
        db.SubmitChanges(); // Uses existing transaction
        db.Transaction.Commit();
    }
    catch { db.Transaction.Rollback(); throw; }
}
```

## SQL Logging

```csharp
db.Log = Console.Out; // Log all generated SQL to console
```

---

## Code Generator Details

### Generated Output

The code generator creates:

1. **DataContext class** — inherits `LiteContext`, with typed `Table<T>` properties
2. **Entity classes** — `[Table]`/`[Column]` attributes, matching your DB schema
3. **FK associations** — navigation property stubs with `[Association]` attributes

### Example Generated Code

```csharp
public partial class RAFInventoryDataContext : LiteContext
{
    public RAFInventoryDataContext(IDbConnection connection) : base(connection) { }
    public RAFInventoryDataContext(string connectionString) : base(connectionString) { }

    public static RAFInventoryDataContext New() => new(DefaultConnectionString);
    public static string DefaultConnectionString { get; set; }

    public Table<tbSYS_User> tbSYS_Users => GetTable<tbSYS_User>();
    public Table<tbINV_StockIn> tbINV_StockIns => GetTable<tbINV_StockIn>();
}

[Table(Name = "dbo.tbSYS_User")]
public partial class tbSYS_User
{
    [Column(Name = "id", DbType = "BIGINT NOT NULL IDENTITY", IsPrimaryKey = true, IsDbGenerated = true)]
    public long id { get; set; }

    [Column(Name = "Username", DbType = "NVARCHAR(100) NOT NULL")]
    public string Username { get; set; }
}
```

### Updating Generated Code

When your database schema changes, re-run the same command to regenerate:

```bash
litesql-codegen -c "Server=.;Database=RAFInventory;..." -n RAF.Models -o Models/dbRAF.cs
```

### Update the Tool

```bash
dotnet tool update --global LiteSql.CodeGen --add-source path/to/LiteSql/nupkg
```

---

## API Compatibility

| LINQ to SQL | LiteSql | Status |
|---|---|---|
| `DataContext` | `LiteContext` | ✅ Drop-in |
| `db.GetTable<T>()` | ✅ Same | Cached per type |
| `table.InsertOnSubmit(e)` | ✅ Same | |
| `table.DeleteOnSubmit(e)` | ✅ Same | |
| `table.Attach(e)` | ✅ Same | 3 overloads |
| `db.SubmitChanges()` | ✅ Same | Auto transaction |
| `db.ExecuteQuery<T>(sql, ...)` | ✅ Same | `{0}` → `@p0` |
| `db.ExecuteCommand(sql, ...)` | ✅ Same | `{0}` → `@p0` |
| `db.Connection` / `Transaction` | ✅ Same | |
| `[Table]`, `[Column]` | ✅ Same | Change `using` only |
| `.Where()`, `.FirstOrDefault()` | ✅ Server-side SQL | Via WhereBuilder |
| `.Single()`, `.SingleOrDefault()` | ✅ Server-side SQL | Instance methods |
| `.First()` | ✅ Server-side SQL | Instance method |
| Update tracking | ✅ Snapshot-based | Auto-detect |

### Beyond L2S (EF Core-inspired)

| Feature | Method |
|---|---|
| Primary key lookup | `Find()` / `FindAsync()` |
| Skip tracking | `AsNoTracking()` |
| FK auto-load | Automatic via `[Association]` |
| Selective FK | `Include(x => x.Nav)` |
| DataLoadOptions | `LoadWith<T>()` |
| Sorting | `OrderBy()`, `OrderByDescending()`, `ThenBy()`, `ThenByDescending()` |
| Pagination | `Skip()`, `Take()` |
| Select Projection | `Select<TResult>()`, `SelectAsync<TResult>()` |
| Dirty Update | Only changed columns are UPDATEd |
| Insert + Get ID | `InsertAndGetId<T>()`, `InsertAndGetIdAsync<T>()` |
| Transaction Helper | `ExecuteInTransaction()`, `ExecuteInTransactionAsync()` |
| Single entity | `Single()`, `SingleOrDefault()`, `First()` |
| Async queries | `WhereAsync()`, `FirstOrDefaultAsync()`, `SingleAsync()`, `SingleOrDefaultAsync()`, `FirstAsync()`, `CountAsync()`, `AnyAsync()`, `ToListAsync()` |
| Async submit | `SubmitChangesAsync()` |
| Async raw SQL | `ExecuteQueryAsync()`, `ExecuteCommandAsync()` |

## Performance vs LINQ to SQL

Benchmark: 100 orders × 2 FK relations (SQLite in-memory)

| Mode | LiteSql Queries | L2S Queries (N+1) | Speedup |
|---|---|---|---|
| **Include(1 FK)** | **2** | 101 | **50x fewer** |
| **Default (all FK)** | **3** | 201 | **67x fewer** |
| **No FK** | **1** | 1 | Same |

| Benchmark | Avg Time (10 runs) |
|---|---|
| Default auto-load (100 orders × 2 FK) | ~19ms |
| Include selective (100 orders × 1 FK) | ~10ms |
| No FK load (100 orders) | ~15ms |
| Async Include (100 orders × 1 FK) | ~10ms |

## Migration Guide

3 steps to migrate from LINQ to SQL:

1. **Replace NuGet**: Remove `System.Data.Linq`, add `LiteSql`
2. **Update `using`**:
   ```diff
   - using System.Data.Linq;
   - using System.Data.Linq.Mapping;
   + using LiteSql;
   + using LiteSql.Mapping;
   ```
3. **Rename base class**:
   ```diff
   - public class MyDb : DataContext
   + public class MyDb : LiteContext
   ```

Or use the Code Generator to regenerate from your database directly.

## Building

```bash
dotnet build
dotnet test   # 204 tests
dotnet pack src/LiteSql/LiteSql.csproj -c Release -o ./nupkg
dotnet pack src/LiteSql.CodeGen/LiteSql.CodeGen.csproj -c Release -o ./nupkg
```

## Limitations & Known Gaps

LiteSql is designed as a **lightweight L2S replacement**, not a full-featured ORM like Entity Framework. Below are features that LiteSql **does not support**:

### Not Supported

| Category | Feature | Description |
|---|---|---|
| **Performance** | SqlBulkCopy | Has batched `BulkInsert()` with chunked INSERT VALUES. No `SqlBulkCopy` for SQL Server yet |
| **Performance** | Split Query | No `AsSplitQuery()`. `Include()` uses batch IN queries (good enough for most cases) |
| **LINQ** | Full LINQ Provider | Has `Where`, `WhereIf`, `AndWhere`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `First`, `Any`, `Count`, `OrderBy`, `ThenBy`, `Skip`, `Take`, `Select`, `Max`, `Min`, `Sum`, `Average`, `Distinct`, `Contains/IN`, `GroupBy` (HAVING, multi-column, conditional/expression aggregates), `Join`/`LeftJoin` (2-table) and `JoinMany`/`LeftJoinMany` (up to 4 tables, WHERE + async), correlated `WhereExists`/`WhereIn` subqueries. No 5+ table joins or subqueries inside SELECT |
| **ORM** | Lazy Loading | No proxy-based or explicit lazy loading |

### By Design (Won't Implement)

| Feature | Reason |
|---|---|
| Full IQueryable Provider | Complexity too high. Use `ExecuteQuery<T>()` for complex queries |
| Database-first Migration | Use SQL scripts or external tools (DbUp, FluentMigrator) |
| Subqueries / 3+ table joins | Use `ExecuteQuery<T>()` / `FromSql<T>()` for complex SQL |

---

## Roadmap

### Completed

- [x] **Phase 1** — Core: GetTable, CRUD, raw SQL, transactions
- [x] **Phase 2** — DBML Code Generator, WhereBuilder, convention mapping
- [x] **Phase 3** — Attach/Detach, server-side queries, update tracking
- [x] **Phase 4** — Full Async API, Find/FindAsync, AsNoTracking
- [x] **Phase 5** — Database Schema CodeGen
- [x] **Phase 6** — FK Navigation, Include API, Performance Tests
- [x] **Phase 7a** — OrderBy/ThenBy, Skip/Take, Dirty Update, SQL Cache, Compiled Delegates
- [x] **Phase 7b** — Select Projection (anonymous, DTO, scalar)
- [x] **Phase 7c** — Aggregates (Max, Min, Sum, Average, Distinct)
- [x] **Phase 8.1** — BulkInsert/BulkInsertAsync (batched INSERT VALUES)
- [x] **Phase 8.2** — InsertAndGetId/InsertAndGetIdAsync
- [x] **Phase 9** — Transaction Helpers (ExecuteInTransaction/Async)
- [x] **Phase 12** — ChangeTracker API (GetState, Entries<T>, IsTracking)
- [x] **Phase 13** — SaveHooks (OnBeforeSave/OnAfterSave lifecycle events)
- [x] **Phase 11** — One-to-Many Collection Navigation (auto-load, Include, batch IN)
- [x] **Phase 7.5** — Compiled Query Cache (expression structural key)
- [x] **Phase 14** — Pagination Helper (PagedResult<T>, ToPagedResult/Async)
- [x] **Phase 15** — Global Query Filters (soft delete, multi-tenant, IgnoreFilters)
- [x] **Phase 16** — Batch Operations (BatchDelete, BatchUpdate, server-side)
- [x] **Phase 17** — Query Profiler (execution timing, slow query detection)
- [x] **Phase 18** — Upsert (INSERT OR REPLACE / MERGE)
- [x] **Phase 19** — Optimistic Concurrency (IsVersion, ConcurrencyException)
- [x] **Phase 20** — FromSql with named parameters
- [x] **Phase 21** — Query Tags (TagWith for SQL debugging)
- [x] **Phase 22** — Async Parity (no-predicate overloads)
- [x] **Phase 23** — SoftDelete convention ([SoftDelete] attribute)
- [x] **Phase 24** — Query Interceptor pipeline
- [x] **Phase 25** — Repository Pattern (IRepository<T>, Repository<T>)
- [x] **Phase 26** — Value Converters (model⇔database type conversion)
- [x] **Phase 27** — Contains/IN clause verification + WhereBuilder improvements
- [x] **Phase 28** — GroupBy (single/multi-column, HAVING, conditional Count, expression aggregates, anonymous-type projection)
- [x] **Phase 29** — Join / LeftJoin (2-table, WHERE predicate over both entities, sync + async)
- [x] **Phase 30** — Dialect-aware query path (SQL Server, SQLite, MySQL, PostgreSQL quoting/pagination/UPSERT)
- [x] **Phase 31** — WhereIf / AndWhere deferred predicate accumulation
- [x] **Phase 32** — LIKE wildcard escaping, multi-DB CodeGen (`--provider`), convention PK detection, composite-key bulk ops, value-converter read path, real optimistic concurrency

All phases complete! 🎉

- [x] **Phase 33** — Correlated subqueries (`WhereExists`/`WhereNotExists`/`WhereIn`/`WhereNotIn`)
- [x] **Phase 34** — Graph insert (`InsertGraph`/`InsertGraphAsync`, parent + child collections, one transaction)
- [x] **Phase 35** — Schema migrations (`Migration`, `SchemaBuilder`, `MigrationRunner` with history + rollback)

### Not Planned

| Feature | Reason |
|---|---|
| Full IQueryable / LINQ Provider | Complexity too high for micro ORM. Use `ExecuteQuery<T>()` for complex SQL |
| Lazy Loading (proxy generation) | Over-engineering, EF Core also recommends avoiding it |
| Fluent Mapping API | Attribute mapping + convention is sufficient |
| Many-to-many relationships | Rare in current codebase. Handle with raw SQL or junction table queries |
| Sharding / Distributed transactions | Application-level concern, not ORM responsibility |
| Second-level cache | Use Redis or `MemoryCache` at application layer |

---

## License

MIT

---

# LiteSql (Tiếng Việt)

> Thư viện thay thế LINQ to SQL cho .NET Core, sử dụng Dapper.

## Tại sao dùng LiteSql?

**LINQ to SQL** chỉ hỗ trợ .NET Framework. **LiteSql** cung cấp API tương thích, bên dưới sử dụng Dapper — giúp code L2S chạy trên .NET Core với thay đổi tối thiểu.

## Cách dùng

### Bước 1: Cài Code Generator

```bash
dotnet tool install --global LiteSql.CodeGen --add-source path/to/LiteSql/nupkg
```

### Bước 2: Gen code từ SQL Server

```bash
litesql-codegen -c "Server=.;Database=MyDb;Trusted_Connection=true;TrustServerCertificate=true" -n MyApp.Models -o Models/MyDb.cs
```

### Bước 3: Add LiteSql vào project

```bash
dotnet add package LiteSql --source path/to/LiteSql/nupkg
```

### Bước 4: Cấu hình startup

```csharp
LiteContext.ConnectionFactory = cs => new SqlConnection(cs);
MyDbDataContext.DefaultConnectionString = "Server=.;Database=MyDb;...";
```

### Bước 5: Dùng

```csharp
using var db = MyDbDataContext.New();
var users = await db.tbSYS_Users.WhereAsync(u => u.Status);
var user  = await db.tbSYS_Users.FindAsync(1);

db.tbSYS_Users.InsertOnSubmit(new tbSYS_User { Username = "test" });
await db.SubmitChangesAsync();
```

## Tính năng

- 🔄 **API giống L2S** — `GetTable<T>()`, `InsertOnSubmit()`, `SubmitChanges()`
- ⚡ **Full Async** — `SubmitChangesAsync()`, `WhereAsync()`, `FindAsync()`
- 🔍 **Server-side query** — `Where()` dịch LINQ → SQL WHERE
- 📊 **Sorting & Pagination** — `OrderBy()`, `ThenBy()`, `Skip()`, `Take()`
- 🧲 **Find by PK** — `Find()` / `FindAsync()`
- 🚀 **AsNoTracking** — Bỏ tracking cho query read-only
- 🔗 **FK Navigation** — Auto-load FK entities, batch IN query
- 🎯 **Include()** — Selective FK loading per-query
- 📊 **Dirty Update** — Chỉ UPDATE cột thay đổi, giảm data transfer
- 🛠️ **Code Gen** — Gen code trực tiếp từ SQL Server hoặc `.dbml`
- 🔌 **Đa DB** — SQL Server + SQLite
- 🔑 **CodeGen Keyword Escaping** — Tự động escape C# reserved keywords (`From` → `@From`)
- 🧪 **204 tests** — Unit, integration & performance

## Hạn chế

LiteSql được thiết kế là **thay thế nhẹ cho L2S**, không phải ORM đầy đủ như Entity Framework.

| Nhóm | Feature | Mô tả |
|---|---|---|
| **Schema** | Migration | Không có migration. Schema quản lý bằng SQL scripts bên ngoài |
| **Hiệu năng** | SqlBulkCopy | Có batched `BulkInsert()` (INSERT VALUES). Chưa có `SqlBulkCopy` |
| **LINQ** | Full LINQ | Có `Where`, `WhereIf`, `AndWhere`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `First`, `Any`, `Count`, `OrderBy`, `ThenBy`, `Skip`, `Take`, `Select`, aggregates, `Contains/IN`, `GroupBy` (HAVING, multi-column, conditional/expression aggregate), `Join`/`LeftJoin` (2 bảng, WHERE + async). Chưa có subquery, join 3+ bảng |
| **ORM** | Graph Object | Không insert/update cả cây object (parent + children) |
| **ORM** | Lazy Loading | Không có lazy loading |

## Lộ trình

### Đã hoàn thành

- [x] **Phase 1–6** — Core, DBML CodeGen, Attach/Detach, Async API, Schema CodeGen, FK Navigation
- [x] **Phase 7** — OrderBy/ThenBy, Skip/Take, Select Projection, Aggregates, Dirty Update
- [x] **Phase 8** — BulkInsert, InsertAndGetId
- [x] **Phase 9** — ExecuteInTransaction/Async
- [x] **Phase 11** — One-to-Many Collection Navigation
- [x] **Phase 12** — ChangeTracker API (GetState, Entries, IsTracking)
- [x] **Phase 13** — SaveHooks (OnBeforeSave/OnAfterSave)
- [x] **Phase 14–27** — Pagination, Global Filters, Batch Operations, Profiler, Upsert, Concurrency, FromSql, Query Tags, SoftDelete, Interceptors, Repository, Value Converters, Contains/IN

Tất cả 27 phase đã hoàn thành! 🎉

> **Ghi chú:** Lazy loading, schema migration, full IQueryable, sharding **không nằm trong kế hoạch** — dùng công cụ ngoài hoặc giải pháp application-level thay thế.
