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
- ⚡ **Full Async API** — `SubmitChangesAsync()`, `WhereAsync()`, `FirstOrDefaultAsync()`, `FindAsync()`
- 🔍 **Server-side filtering** — `Where(predicate)` translates LINQ expressions to SQL WHERE
- 📊 **Sorting & Pagination** — `OrderBy()`, `ThenBy()`, `Skip()`, `Take()` — server-side SQL
- 🧲 **Find by PK** — `Find()` / `FindAsync()` for efficient primary key lookups
- 🚀 **AsNoTracking** — Skip change tracking for read-only queries
- 📊 **Dirty Update** — Only changed columns are UPDATEd, reducing data transfer
- 🎯 **Same mapping attributes** — `[Table]`, `[Column]` with identical signatures
- 🔌 **Multi-database** — SQL Server and SQLite, extensible to others
- 📦 **.NET Standard 2.0** — Works on both .NET Framework and .NET Core / .NET 5+
- 🛠️ **Code Generator** — Generate entities from SQL Server database or `.dbml` files
- 🧪 **146 tests** — Unit, integration & performance tests with SQLite in-memory

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
litesql-codegen -c <connection-string> [options] # from SQL Server

Options:
  -o, --output <path>       Output .cs file path
  -n, --namespace <ns>      Target namespace (default: Models)
  -c, --connection <cs>     SQL Server connection string
      --context <name>      Override context class name
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
| Async queries | `WhereAsync()`, `FirstOrDefaultAsync()`, `CountAsync()`, `AnyAsync()`, `ToListAsync()` |
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
dotnet test   # 146 tests
dotnet pack src/LiteSql/LiteSql.csproj -c Release -o ./nupkg
dotnet pack src/LiteSql.CodeGen/LiteSql.CodeGen.csproj -c Release -o ./nupkg
```

## Limitations & Known Gaps

LiteSql is designed as a **lightweight L2S replacement**, not a full-featured ORM like Entity Framework. Below are features that LiteSql **does not support**:

### Not Supported

| Category | Feature | Description |
|---|---|---|
| **Schema** | Migration | No `Add-Migration` / `Update-Database`. Schema managed externally (SQL scripts, SSMS). CodeGen is DB → Code only |
| **Performance** | Bulk Insert | `BulkInsert()` with batched INSERT VALUES. No `SqlBulkCopy` for SQL Server yet |
| **Performance** | Split Query | No `AsSplitQuery()`. `Include()` uses batch IN queries (good enough for most cases) |
| **LINQ** | Full LINQ Provider | `Where`, `FirstOrDefault`, `Any`, `Count`, `OrderBy`, `ThenBy`, `Skip`, `Take`, `Select`, `Max`, `Min`, `Sum`, `Average`, `Distinct`. No `GroupBy`, `Join` |
| **Transaction** | Transaction Helpers | Has basic `db.Transaction` + auto-transaction in `SubmitChanges`. No `ExecuteInTransaction(action)`, `SavePoint`, or `TransactionScope` |
| **ORM** | Graph Insert/Update | Cannot insert/update an entire object graph (parent + children) in one call |
| **ORM** | Collection Navigation | FK navigation is parent-only (many-to-one). No `Order.OrderDetails` (one-to-many) collections |
| **ORM** | ChangeTracker API | Has snapshot-based update tracking, but no `ChangeTracker.Entries()` to query entity states at runtime |
| **ORM** | Interceptors / Hooks | No `SaveChanges` interceptors, query filters, or soft-delete hooks |
| **ORM** | Lazy Loading | No proxy-based or explicit lazy loading |

### By Design (Won't Implement)

| Feature | Reason |
|---|---|
| Full IQueryable Provider | Complexity too high. Use `ExecuteQuery<T>()` for complex queries |
| Database-first Migration | Use SQL scripts or external tools (DbUp, FluentMigrator) |
| Global Query Filters | Can be worked around with `Where()` or raw SQL |

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

All planned phases are complete! 🎉
- [ ] **Phase 8 — Bulk & Batch Operations** ⭐ High Priority
  - `InsertAllOnSubmit(IEnumerable<T>)` with `SqlBulkCopy` backend
  - `BulkInsert<T>(IEnumerable<T>)` — Direct bulk insert (no tracking)
  - `BulkUpdate<T>()`, `BulkDelete<T>()` — Batch DML
  - Batch command execution — Combine multiple commands per roundtrip
  - `InsertAndGetId<T>()` — Insert and return generated identity
- [ ] **Phase 9 — Transaction & Unit of Work**
  - `db.ExecuteInTransaction(action)` — Auto commit/rollback wrapper
  - `db.ExecuteInTransactionAsync(func)` — Async variant
  - `Savepoint` support for nested operations
  - Improved `SubmitChanges()` batching — Group insert/update/delete by type
- [ ] **Phase 10 — ChangeTracker & Entity State**
  - `EntityState` enum: `Unchanged`, `Added`, `Modified`, `Deleted`
  - `db.ChangeTracker.Entries<T>()` — Query tracked entities and their states
  - `db.ChangeTracker.HasChanges()` — Quick dirty check
- [ ] **Phase 11 — Relationship & Navigation**
  - One-to-many navigation (`Order.OrderDetails`)
  - `Include(x => x.Children)` for collection loading
  - `ThenInclude()` — Nested multi-level eager loading
  - Graph insert — Save parent + children in one `SubmitChanges()` (1 level)
- [ ] **Phase 12 — Hooks & Filters**
  - `BeforeSave` / `AfterSave` interceptor hooks
  - Global query filters — Soft delete (`IsDeleted = false`), multi-tenant
  - Audit auto-fill — `CreatedDate`, `UpdatedDate`, `CreatedBy` on save
  - Value converters — Enum ↔ string, JSON column mapping
- [ ] **Phase 13 — Quality of Life**
  - `Upsert<T>()` — Insert or update (MERGE / ON CONFLICT)
  - `ExecuteScalar<T>()` — Single value queries
  - PostgreSQL provider support
  - Enhanced logging & diagnostics — Query timing, parameter logging

### Not Planned

| Feature | Reason |
|---|---|
| Full IQueryable / LINQ Provider | Complexity too high for micro ORM. Use `ExecuteQuery<T>()` for complex SQL |
| Schema Migration | Use external tools: DbUp, FluentMigrator, or SQL scripts |
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
- 🧪 **97 tests** — Unit, integration & performance

## Hạn chế

LiteSql được thiết kế là **thay thế nhẹ cho L2S**, không phải ORM đầy đủ như Entity Framework.

| Nhóm | Feature | Mô tả |
|---|---|---|
| **Schema** | Migration | Không có migration. Schema quản lý bằng SQL scripts bên ngoài |
| **Hiệu năng** | Bulk Insert | Không có `SqlBulkCopy`. Insert từng row |
| **LINQ** | Full LINQ | Có `Where`, `FirstOrDefault`, `Any`, `Count`, `OrderBy`, `ThenBy`, `Skip`, `Take`, `Select`. Chưa có `GroupBy`, `Join` |
| **Transaction** | Helpers | Có cơ bản. Chưa có `ExecuteInTransaction()` |
| **ORM** | Graph Object | Không insert/update cả cây object (parent + children) |
| **ORM** | Collection Nav | FK navigation chỉ 1 chiều (many-to-one). Chưa có `Order.OrderDetails` |
| **ORM** | ChangeTracker API | Có snapshot tracking, chưa có API query trạng thái entity |

## Lộ trình

### Đã hoàn thành

- [x] **Phase 1** — Core: GetTable, CRUD, raw SQL, transactions
- [x] **Phase 2** — DBML Code Generator, WhereBuilder, convention mapping
- [x] **Phase 3** — Attach/Detach, server-side queries, update tracking
- [x] **Phase 4** — Full Async API, Find/FindAsync, AsNoTracking
- [x] **Phase 5** — Database Schema CodeGen
- [x] **Phase 6** — FK Navigation, Include API, Performance Tests
- [x] **Phase 7a** — OrderBy/ThenBy, Skip/Take, Dirty Update, SQL Cache, Compiled Delegates
- [x] **Phase 7b** — Select Projection, InsertAndGetId, Transaction Helpers

### Dự kiến (theo độ ưu tiên)

- [ ] **Phase 7c — Mở rộng LINQ (tt.)** ⭐ — aggregates, query cache
- [ ] **Phase 8 — Bulk & Batch** ⭐ — `SqlBulkCopy`, `BulkUpdate`, `BulkDelete`, `InsertAndGetId()`
- [ ] **Phase 9 — Transaction & Unit of Work** — `ExecuteInTransaction()`, Savepoint, batch submit
- [ ] **Phase 10 — ChangeTracker & Entity State** — `EntityState`, `Entries<T>()`, `HasChanges()`
- [ ] **Phase 11 — Relationship & Navigation** — One-to-many, `ThenInclude()`, graph insert
- [ ] **Phase 12 — Hooks & Filters** — `BeforeSave`/`AfterSave`, soft delete filter, audit auto-fill
- [ ] **Phase 13 — Tiện ích** — `Upsert()`, `ExecuteScalar()`, PostgreSQL, diagnostics

> **Ghi chú:** Lazy loading, schema migration, full IQueryable, sharding **không nằm trong kế hoạch** — dùng công cụ ngoài hoặc giải pháp application-level thay thế.
