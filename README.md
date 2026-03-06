# LiteSql

> A lightweight LINQ to SQL replacement for .NET Core, powered by Dapper.

[![.NET Standard 2.0](https://img.shields.io/badge/.NET%20Standard-2.0-blue)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Why LiteSql?

**LINQ to SQL** (`System.Data.Linq`) is a .NET Framework-only ORM with no .NET Core support. Migrating large codebases away from it is painful and risky.

**LiteSql** bridges the gap by providing a **drop-in compatible API** backed by [Dapper](https://github.com/DapperLib/Dapper) — so your existing L2S patterns keep working on .NET Core with minimal code changes.

### Key Features

- 🔄 **L2S-compatible API** — `GetTable<T>()`, `InsertOnSubmit()`, `DeleteOnSubmit()`, `SubmitChanges()`, `ExecuteQuery<T>()`, `ExecuteCommand()`
- ⚡ **Dapper-powered** — High performance query execution and object mapping
- 🎯 **Same mapping attributes** — `[Table]`, `[Column]` with identical signatures; just change your `using`
- 🔌 **Multi-database** — SQL Server, SQLite, and extensible to others
- 📦 **.NET Standard 2.0** — Works on both .NET Framework and .NET Core / .NET 5+
- 🧪 **Well-tested** — 34 unit & integration tests

## Quick Start

### Installation

```bash
# Coming soon to NuGet
dotnet add package LiteSql
```

### Basic Usage

```csharp
// 1. Configure connection factory (once at startup)
LiteContext.ConnectionFactory = cs => new SqlConnection(cs);

// 2. Use it just like DataContext
using (var db = new LiteContext("your-connection-string"))
{
    // Query
    var users = db.GetTable<User>().Where(u => u.IsActive).ToList();

    // Insert
    var newUser = new User { Name = "Alice", Email = "alice@example.com" };
    db.GetTable<User>().InsertOnSubmit(newUser);

    // Delete
    var oldUser = db.GetTable<User>().FirstOrDefault(u => u.Name == "Bob");
    db.GetTable<User>().DeleteOnSubmit(oldUser);

    // Commit all changes in a single transaction
    db.SubmitChanges();
}
```

### Raw SQL (L2S-style positional parameters)

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

### Entity Mapping

Use the **same attributes** as LINQ to SQL — just change the `using` from `System.Data.Linq.Mapping` to `LiteSql.Mapping`:

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

Or use **convention-based mapping** (no attributes needed):

```csharp
[Table(Name = "Logs")]
public class LogEntry
{
    public int Id { get; set; }      // Mapped to "Id" column
    public string Message { get; set; } // Mapped to "Message" column  
    public string Level { get; set; }   // Mapped to "Level" column
}
```

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
        db.SubmitChanges(); // Uses existing transaction

        db.Transaction.Commit();
    }
    catch
    {
        db.Transaction.Rollback();
        throw;
    }
}
```

### SQL Logging

```csharp
using (var db = new LiteContext(connection))
{
    db.Log = Console.Out; // Log all generated SQL to console
    // ... your operations
}
```

## API Compatibility

| LINQ to SQL | LiteSql | Notes |
|---|---|---|
| `DataContext` | `LiteContext` | Drop-in replacement |
| `db.GetTable<T>()` | ✅ Same | Cached per type |
| `table.InsertOnSubmit(e)` | ✅ Same | |
| `table.InsertAllOnSubmit(...)` | ✅ Same | |
| `table.DeleteOnSubmit(e)` | ✅ Same | |
| `table.DeleteAllOnSubmit(...)` | ✅ Same | |
| `db.SubmitChanges()` | ✅ Same | Auto-wraps in transaction |
| `db.ExecuteQuery<T>(sql, ...)` | ✅ Same | `{0}` → `@p0` conversion |
| `db.ExecuteCommand(sql, ...)` | ✅ Same | `{0}` → `@p0` conversion |
| `db.Connection` | ✅ Same | |
| `db.Transaction` | ✅ Same | |
| `db.CommandTimeout` | ✅ Same | |
| `db.Log` | ✅ Same | |
| `[Table]`, `[Column]` | ✅ Same signatures | Change `using` only |
| LINQ `.Where()`, `.FirstOrDefault()`, etc. | ✅ Via `IEnumerable<T>` | In-memory filtering (Phase 1) |
| Lazy loading | ❌ Not yet | Planned for Phase 3 |
| Association navigation | ❌ Not yet | Planned for Phase 3 |
| Update tracking | ❌ Not yet | Planned for Phase 2 |

## Migration Guide

Migrating from LINQ to SQL to LiteSql takes 3 steps:

1. **Replace NuGet reference**: Remove `System.Data.Linq`, add `LiteSql`
2. **Update `using` statements**:
   ```diff
   - using System.Data.Linq;
   - using System.Data.Linq.Mapping;
   + using LiteSql;
   + using LiteSql.Mapping;
   ```
3. **Rename your context base class**:
   ```diff
   - public class MyDb : DataContext
   + public class MyDb : LiteContext
   ```

Your model classes, `GetTable<T>()` calls, `InsertOnSubmit()`, `DeleteOnSubmit()`, `SubmitChanges()`, and raw SQL queries should work without any changes.

## Roadmap

- [x] **Phase 1** — Core foundation (GetTable, CRUD, raw SQL, transactions)
- [ ] **Phase 2** — Expression → SQL translation (`WhereBuilder`), `IQueryable<T>` provider, update tracking
- [ ] **Phase 3** — Association navigation, lazy loading, stored procedure mapping

## Building

```bash
dotnet build
dotnet test
```

## License

MIT

---

# LiteSql (Tiếng Việt)

> Thư viện thay thế LINQ to SQL cho .NET Core, sử dụng Dapper.

## Tại sao dùng LiteSql?

**LINQ to SQL** (`System.Data.Linq`) chỉ hỗ trợ .NET Framework, không dùng được trên .NET Core. Việc migrate codebase lớn khỏi L2S tốn nhiều công sức và rủi ro.

**LiteSql** giải quyết vấn đề này bằng cách cung cấp **API tương thích** với L2S, bên dưới sử dụng [Dapper](https://github.com/DapperLib/Dapper) — giúp code L2S hiện tại chạy trên .NET Core với thay đổi tối thiểu.

### Tính năng chính

- 🔄 **API giống L2S** — `GetTable<T>()`, `InsertOnSubmit()`, `DeleteOnSubmit()`, `SubmitChanges()`, `ExecuteQuery<T>()`, `ExecuteCommand()`
- ⚡ **Dapper engine** — Truy vấn và mapping hiệu năng cao
- 🎯 **Cùng mapping attributes** — `[Table]`, `[Column]` cùng signature, chỉ cần đổi `using`
- 🔌 **Đa database** — SQL Server, SQLite, mở rộng được
- 📦 **.NET Standard 2.0** — Chạy trên cả .NET Framework và .NET Core / .NET 5+

## Hướng dẫn migrate

3 bước đơn giản:

1. **Thay NuGet**: Bỏ `System.Data.Linq`, thêm `LiteSql`
2. **Đổi `using`**: `System.Data.Linq` → `LiteSql`, `System.Data.Linq.Mapping` → `LiteSql.Mapping`
3. **Đổi base class**: `DataContext` → `LiteContext`

Model classes, `GetTable<T>()`, `InsertOnSubmit()`, `DeleteOnSubmit()`, `SubmitChanges()`, raw SQL — **giữ nguyên, không cần sửa**.

## Lộ trình phát triển

- [x] **Phase 1** — Core: GetTable, CRUD, raw SQL, transactions *(hoàn thành)*
- [ ] **Phase 2** — Expression → SQL WHERE, `IQueryable<T>`, update tracking
- [ ] **Phase 3** — Association navigation, lazy loading, stored procedure mapping
