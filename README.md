# LiteSql

> A lightweight LINQ to SQL replacement for .NET Core, powered by Dapper.

[![.NET Standard 2.0](https://img.shields.io/badge/.NET%20Standard-2.0-blue)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Why LiteSql?

**LINQ to SQL** (`System.Data.Linq`) is a .NET Framework-only ORM with no .NET Core support. Migrating large codebases away from it is painful and risky.

**LiteSql** bridges the gap by providing a **drop-in compatible API** backed by [Dapper](https://github.com/DapperLib/Dapper) — so your existing L2S patterns keep working on .NET Core with minimal code changes.

### Key Features

- 🔄 **L2S-compatible API** — `GetTable<T>()`, `InsertOnSubmit()`, `DeleteOnSubmit()`, `SubmitChanges()`
- ⚡ **Full Async API** — `SubmitChangesAsync()`, `WhereAsync()`, `FirstOrDefaultAsync()`, `FindAsync()`
- 🔍 **Server-side filtering** — `Where(predicate)` translates LINQ expressions to SQL WHERE
- 🧲 **Find by PK** — `Find()` / `FindAsync()` for efficient primary key lookups
- 🚀 **AsNoTracking** — Skip change tracking for read-only queries
- 📊 **Update tracking** — Snapshot-based change detection on `SubmitChanges()`
- 🎯 **Same mapping attributes** — `[Table]`, `[Column]` with identical signatures
- 🔌 **Multi-database** — SQL Server and SQLite, extensible to others
- 📦 **.NET Standard 2.0** — Works on both .NET Framework and .NET Core / .NET 5+
- 🛠️ **DBML Code Generator** — Generate entity classes from existing `.dbml` files
- 🧪 **67 tests** — Unit & integration tests with SQLite in-memory

## Quick Start

### Basic Usage

```csharp
// Configure once at startup
LiteContext.ConnectionFactory = cs => new SqlConnection(cs);

using (var db = new MyDataContext("your-connection-string"))
{
    // Server-side queries (LINQ → SQL WHERE)
    var users = db.tbSYS_Users.Where(u => u.IsActive).ToList();

    // Find by primary key
    var user = db.tbSYS_Users.Find(userId);

    // Insert
    db.tbSYS_Users.InsertOnSubmit(new tbSYS_User { Name = "Alice" });

    // Modify loaded entity (auto-detected)
    user.Email = "newemail@example.com";

    // Commit all changes
    db.SubmitChanges();
}
```

### Async API

```csharp
using (var db = new MyDataContext("connection-string"))
{
    // All query methods have async counterparts
    var users = await db.tbSYS_Users.WhereAsync(u => u.IsActive);
    var user  = await db.tbSYS_Users.FirstOrDefaultAsync(u => u.Name == "Alice");
    var count = await db.tbSYS_Users.CountAsync(u => u.Status);
    var exists = await db.tbSYS_Users.AnyAsync(u => u.Email == email);
    var all   = await db.tbSYS_Users.ToListAsync();
    var found = await db.tbSYS_Users.FindAsync(userId);

    // Async submit
    db.tbSYS_Users.InsertOnSubmit(newUser);
    await db.SubmitChangesAsync();

    // Async raw SQL
    var results = await db.ExecuteQueryAsync<Product>("SELECT * FROM Products WHERE Price > {0}", 100);
    var affected = await db.ExecuteCommandAsync("UPDATE Products SET Price = {0} WHERE Id = {1}", price, id);
}
```

### Read-Only Queries (AsNoTracking)

```csharp
// Skip change tracking for better performance on read-only queries
var items = db.GetTable<Product>().AsNoTracking()
    .Where(p => p.IsActive)
    .ToList();
// Modifications to these entities will NOT be saved on SubmitChanges
```

### Server-Side Filtering

```csharp
// These expressions are translated to SQL (not in-memory):
db.Products.Where(p => p.Price > 100 && p.IsActive);
// → SELECT * FROM [Products] WHERE [Price] > @w0 AND [IsActive] = @w1

db.Products.Where(p => p.Name.Contains("Widget"));
// → SELECT * FROM [Products] WHERE [Name] LIKE @w0  ('%Widget%')

var ids = new List<int> { 1, 2, 3 };
db.Products.Where(p => ids.Contains(p.CategoryId));
// → SELECT * FROM [Products] WHERE [CategoryId] IN (@w0, @w1, @w2)

db.Products.FirstOrDefault(p => p.Id == 1);
// → SELECT TOP 1 * FROM [Products] WHERE [Id] = @w0  (LIMIT 1 for SQLite)
```

### Entity Mapping

Use the **same attributes** as LINQ to SQL — just change `using`:

```csharp
using LiteSql.Mapping;

[Table(Name = "dbo.Products")]
public class Product
{
    [Column(Name = "ProductId", IsPrimaryKey = true, IsDbGenerated = true)]
    public int Id { get; set; }

    [Column(Name = "ProductName")]
    public string Name { get; set; }

    [Column(Name = "UnitPrice", CanBeNull = true)]
    public decimal? Price { get; set; }
}
```

### DBML Code Generator

Generate LiteSql-compatible C# from existing `.dbml` files:

```bash
dotnet run --project src/LiteSql.CodeGen -- dbRAF.dbml -o Models/dbRAF.cs -n RAF.Models
```

The tool generates:
- Entity classes with `[Table]`/`[Column]` attributes
- Typed `Table<T>` properties (`db.tbSYS_Users`, `db.tbINV_StockIns`, etc.)
- A `LiteContext` subclass matching your existing DataContext

### Transaction Support

```csharp
using (var db = new LiteContext(connection))
{
    db.Connection.Open();
    db.Transaction = db.Connection.BeginTransaction();
    try
    {
        db.GetTable<Order>().InsertOnSubmit(order);
        db.GetTable<OrderDetail>().InsertAllOnSubmit(details);
        db.SubmitChanges();
        db.Transaction.Commit();
    }
    catch { db.Transaction.Rollback(); throw; }
}
```

## API Compatibility

| LINQ to SQL | LiteSql | Status |
|---|---|---|
| `DataContext` | `LiteContext` | ✅ Drop-in |
| `db.GetTable<T>()` | ✅ Same | Cached per type |
| `table.InsertOnSubmit(e)` | ✅ Same | |
| `table.DeleteOnSubmit(e)` | ✅ Same | |
| `db.SubmitChanges()` | ✅ Same | Auto transaction |
| `db.ExecuteQuery<T>(sql, ...)` | ✅ Same | `{0}` → `@p0` |
| `db.ExecuteCommand(sql, ...)` | ✅ Same | `{0}` → `@p0` |
| `db.Connection` / `Transaction` | ✅ Same | |
| `[Table]`, `[Column]` | ✅ Same | Change `using` only |
| `.Where()`, `.FirstOrDefault()` | ✅ Server-side SQL | Via WhereBuilder |
| `table.Attach(e)` | ✅ Same | 3 overloads |
| Update tracking | ✅ Snapshot-based | Auto-detect on `SubmitChanges` |

### Beyond L2S (EF Core-inspired)

| Feature | Description |
|---|---|
| `FindAsync()` / `Find()` | Primary key lookup |
| `AsNoTracking()` | Skip change tracking for reads |
| `WhereAsync()` | Async server-side filtering |
| `FirstOrDefaultAsync()` | Async with TOP/LIMIT |
| `CountAsync()` / `AnyAsync()` | Async aggregate queries |
| `ToListAsync()` | Async load all |
| `SubmitChangesAsync()` | Async CRUD submission |

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

## Building

```bash
dotnet build
dotnet test   # 67 tests
```

## License

MIT

---

# LiteSql (Tiếng Việt)

> Thư viện thay thế LINQ to SQL cho .NET Core, sử dụng Dapper.

## Tại sao dùng LiteSql?

**LINQ to SQL** chỉ hỗ trợ .NET Framework. **LiteSql** cung cấp API tương thích, bên dưới sử dụng Dapper — giúp code L2S chạy trên .NET Core với thay đổi tối thiểu.

### Tính năng

- 🔄 **API giống L2S** — `GetTable<T>()`, `InsertOnSubmit()`, `SubmitChanges()`
- ⚡ **Full Async** — `SubmitChangesAsync()`, `WhereAsync()`, `FindAsync()`
- 🔍 **Server-side** — `Where()` dịch LINQ → SQL WHERE
- 🧲 **Find by PK** — `Find()` / `FindAsync()`
- 🚀 **AsNoTracking** — Bỏ tracking cho query read-only
- 📊 **Update tracking** — Tự phát hiện thay đổi entity
- 🛠️ **DBML Code Gen** — Sinh code từ `.dbml` sẵn có
- 🔌 **Đa DB** — SQL Server + SQLite
- 🧪 **67 tests** — Unit & integration

## Hướng dẫn migrate

1. **Thay NuGet**: `System.Data.Linq` → `LiteSql`
2. **Đổi `using`**: `System.Data.Linq` → `LiteSql`, `System.Data.Linq.Mapping` → `LiteSql.Mapping`
3. **Đổi base class**: `DataContext` → `LiteContext`

Code hiện tại — `GetTable<T>()`, `InsertOnSubmit()`, `SubmitChanges()`, raw SQL — **giữ nguyên**.

## Lộ trình

- [x] **Phase 1** — Core: GetTable, CRUD, raw SQL, transactions
- [x] **Phase 2** — DBML Code Generator, WhereBuilder, Update Tracking
- [x] **Phase 3** — Attach/Detach, Server-side FirstOrDefault/Count/Any
- [x] **Phase 4** — Full Async API, Find/FindAsync, AsNoTracking
