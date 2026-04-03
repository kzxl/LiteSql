# ORM Architecture Research: linq2db, SqlSugar, FreeSql

## Overview

Nghiên cứu kiến trúc và design patterns từ các ORM thành công để cải thiện LiteSql.

---

## 1. linq2db Architecture

### Core Design Principles

**Expression Tree Translation**
```csharp
// linq2db approach
public interface IQueryable<T>
{
    IQueryable<T> Where(Expression<Func<T, bool>> predicate);
    IQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);
    IQueryable<IGrouping<TKey, T>> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector);
}

// Visitor pattern for SQL generation
public class SqlBuilder : ExpressionVisitor
{
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Translate LINQ methods to SQL
    }
}
```

**Key Features:**
1. **Strongly Typed** - Full compile-time safety
2. **Expression Caching** - Cache compiled expressions
3. **Multi-Database** - Abstract SQL dialect differences
4. **Bulk Operations** - Optimized batch inserts/updates
5. **Mapping Flexibility** - Fluent API + Attributes

### Builder Pattern

```csharp
// linq2db's fluent query builder
var query = db.GetTable<Person>()
    .Where(p => p.Age > 18)
    .GroupBy(p => p.City)
    .Select(g => new {
        City = g.Key,
        Count = g.Count(),
        AvgAge = g.Average(p => p.Age)
    })
    .Having(g => g.Count > 10)  // HAVING support
    .OrderBy(x => x.Count);

// SQL Generation
// SELECT City, COUNT(*), AVG(Age)
// FROM Person
// WHERE Age > 18
// GROUP BY City
// HAVING COUNT(*) > 10
// ORDER BY COUNT(*)
```

**Strengths:**
- ✅ Clean, intuitive API
- ✅ Excellent performance
- ✅ Multi-database support
- ✅ Expression caching
- ✅ Bulk operations

**Weaknesses:**
- ⚠️ Complex codebase
- ⚠️ Steep learning curve
- ⚠️ Heavy dependencies

---

## 2. SqlSugar Architecture

### Core Design Principles

**Queryable Builder Pattern**
```csharp
// SqlSugar's approach - more explicit
var query = db.Queryable<Order>()
    .Where(o => o.Status == "Completed")
    .GroupBy(o => o.CustomerId)
    .Select(g => new {
        CustomerId = g.CustomerId,
        TotalAmount = SqlFunc.AggregateSum(g.Amount),
        AvgAmount = SqlFunc.AggregateAvg(g.Amount),
        Count = SqlFunc.AggregateCount(g.Id)
    })
    .Having(g => SqlFunc.AggregateSum(g.Amount) > 1000)
    .OrderBy(x => x.TotalAmount, OrderByType.Desc);
```

**Key Features:**
1. **SqlFunc Helper** - Explicit SQL functions
2. **Multi-Database** - Support 10+ databases
3. **Code First** - Auto create/update tables
4. **Easy to Learn** - Simple, consistent API
5. **Chinese Documentation** - Popular in China

### Queryable Architecture

```csharp
// SqlSugar's Queryable interface
public interface ISugarQueryable<T>
{
    ISugarQueryable<T> Where(Expression<Func<T, bool>> expression);
    ISugarQueryable<T> WhereIF(bool isWhere, Expression<Func<T, bool>> expression);
    ISugarQueryable<T> OrderBy(Expression<Func<T, object>> expression);
    ISugarQueryable<T> GroupBy(Expression<Func<T, object>> expression);
    ISugarQueryable<T> Having(Expression<Func<T, bool>> expression);
    ISugarQueryable<T> Select<TResult>(Expression<Func<T, TResult>> expression);
    
    // Pagination
    ISugarQueryable<T> Take(int num);
    ISugarQueryable<T> Skip(int num);
    
    // Execution
    List<T> ToList();
    Task<List<T>> ToListAsync();
    T First();
    T Single();
}
```

**Strengths:**
- ✅ Very easy to learn
- ✅ Consistent API across databases
- ✅ WhereIF pattern (conditional where)
- ✅ Excellent Chinese docs
- ✅ Active community

**Weaknesses:**
- ⚠️ Less type-safe (SqlFunc strings)
- ⚠️ Performance overhead
- ⚠️ Limited English docs

---

## 3. FreeSql Architecture

### Core Design Principles

**ISelect Builder Pattern**
```csharp
// FreeSql's approach - most flexible
var query = fsql.Select<Order>()
    .Where(o => o.Status == "Completed")
    .GroupBy(o => new { o.CustomerId, o.Year })
    .Having(g => g.Sum(o => o.Amount) > 1000)
    .OrderBy(g => g.Sum(o => o.Amount))
    .ToList(g => new {
        CustomerId = g.Key.CustomerId,
        Year = g.Key.Year,
        TotalAmount = g.Sum(o => o.Amount),
        AvgAmount = g.Avg(o => o.Amount),
        Count = g.Count()
    });
```

**Key Features:**
1. **ISelect Interface** - Separate query builder
2. **Repository Pattern** - Built-in repository support
3. **Multi-Database** - 10+ databases
4. **AOP Support** - Aspect-oriented programming
5. **Sharding** - Built-in table/database sharding

### ISelect Architecture

```csharp
// FreeSql's ISelect interface
public interface ISelect<T1>
{
    ISelect<T1> Where(Expression<Func<T1, bool>> exp);
    ISelect<T1> WhereIf(bool condition, Expression<Func<T1, bool>> exp);
    
    // GroupBy returns ISelectGrouping
    ISelectGrouping<TKey> GroupBy<TKey>(Expression<Func<T1, TKey>> exp);
    
    ISelect<T1> OrderBy<TMember>(Expression<Func<T1, TMember>> column);
    ISelect<T1> OrderByDescending<TMember>(Expression<Func<T1, TMember>> column);
    
    // Pagination
    ISelect<T1> Page(int pageNumber, int pageSize);
    ISelect<T1> Skip(int offset);
    ISelect<T1> Take(int limit);
    
    // Execution
    List<T1> ToList();
    Task<List<T1>> ToListAsync();
    T1 First();
    T1 ToOne();
    
    // SQL
    string ToSql();
}

// ISelectGrouping for GROUP BY
public interface ISelectGrouping<TKey>
{
    ISelectGrouping<TKey> Having(Expression<Func<ISelectGroupingAggregate<TKey>, bool>> exp);
    ISelectGrouping<TKey> OrderBy<TMember>(Expression<Func<ISelectGroupingAggregate<TKey>, TMember>> column);
    
    List<TReturn> ToList<TReturn>(Expression<Func<ISelectGroupingAggregate<TKey>, TReturn>> select);
    Task<List<TReturn>> ToListAsync<TReturn>(Expression<Func<ISelectGroupingAggregate<TKey>, TReturn>> select);
}
```

**Strengths:**
- ✅ Most flexible API
- ✅ Excellent performance
- ✅ Built-in sharding
- ✅ Repository pattern
- ✅ AOP support
- ✅ ToSql() for debugging

**Weaknesses:**
- ⚠️ Complex API surface
- ⚠️ Steeper learning curve
- ⚠️ Less documentation

---

## 4. Comparison Matrix

| Feature | linq2db | SqlSugar | FreeSql | LiteSql (Current) |
|---------|---------|----------|---------|-------------------|
| **API Style** | LINQ-first | Queryable | ISelect | Table<T> |
| **Type Safety** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Performance** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Ease of Use** | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Multi-DB** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐ |
| **GroupBy** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Bulk Ops** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐ |
| **Sharding** | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐ |
| **Docs** | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ |

---

## 5. Key Learnings for LiteSql

### 1. Separate GroupBy Query Type (FreeSql Pattern)

**Current LiteSql:**
```csharp
var result = db.Orders
    .GroupBy(o => o.CustomerId)
    .Select(g => new { ... });  // Must call Select immediately
```

**Improved (FreeSql-inspired):**
```csharp
// Return ISelectGrouping instead of forcing Select
public interface IGroupByQuery<T, TKey>
{
    IGroupByQuery<T, TKey> Having(Expression<Func<IGrouping<TKey, T>, bool>> predicate);
    IGroupByQuery<T, TKey> OrderBy<TMember>(Expression<Func<IGrouping<TKey, T>, TMember>> keySelector);
    
    List<TResult> ToList<TResult>(Expression<Func<IGrouping<TKey, T>, TResult>> selector);
    Task<List<TResult>> ToListAsync<TResult>(Expression<Func<IGrouping<TKey, T>, TResult>> selector);
    
    string ToSql<TResult>(Expression<Func<IGrouping<TKey, T>, TResult>> selector);  // Debug
}
```

### 2. WhereIf Pattern (SqlSugar/FreeSql)

**Add conditional query building:**
```csharp
public static class QueryExtensions
{
    public static Table<T> WhereIf<T>(
        this Table<T> table,
        bool condition,
        Expression<Func<T, bool>> predicate) where T : class
    {
        return condition ? table.Where(predicate) : table;
    }
}

// Usage
var query = db.Orders
    .WhereIf(!string.IsNullOrEmpty(status), o => o.Status == status)
    .WhereIf(minAmount.HasValue, o => o.Amount >= minAmount.Value);
```

### 3. ToSql() Method (FreeSql Pattern)

**Add SQL debugging:**
```csharp
public class GroupByQuery<T, TKey>
{
    public string ToSql<TResult>(Expression<Func<IGrouping<TKey, T>, TResult>> selector)
    {
        var (sql, parameters) = BuildQuery(selector);
        
        // Replace parameters for debugging
        foreach (var (key, value) in parameters)
        {
            sql = sql.Replace(key, $"'{value}'");
        }
        
        return sql;
    }
}

// Usage
var sql = db.Orders
    .GroupBy(o => o.CustomerId)
    .ToSql(g => new { g.Key, Count = g.Count() });

Console.WriteLine(sql);
// SELECT [CustomerId], COUNT(*) AS [Count]
// FROM [Orders]
// GROUP BY [CustomerId]
```

### 4. Expression Caching (linq2db Pattern)

**Cache compiled expressions:**
```csharp
public static class ExpressionCache
{
    private static readonly ConcurrentDictionary<int, Delegate> Cache = new();
    
    public static Func<T, TResult> GetOrAdd<T, TResult>(
        Expression<Func<T, TResult>> expression)
    {
        var key = expression.ToString().GetHashCode();
        
        return (Func<T, TResult>)Cache.GetOrAdd(key, _ => expression.Compile());
    }
}
```

### 5. Aggregate Helper (SqlSugar-inspired)

**Type-safe aggregate functions:**
```csharp
public static class Agg
{
    public static int Count<T>(IGrouping<T, object> group) => throw new NotImplementedException();
    public static TValue Sum<T, TValue>(IGrouping<T, object> group, Func<object, TValue> selector) => throw new NotImplementedException();
    public static TValue Avg<T, TValue>(IGrouping<T, object> group, Func<object, TValue> selector) => throw new NotImplementedException();
    public static TValue Min<T, TValue>(IGrouping<T, object> group, Func<object, TValue> selector) => throw new NotImplementedException();
    public static TValue Max<T, TValue>(IGrouping<T, object> group, Func<object, TValue> selector) => throw new NotImplementedException();
}

// Usage (alternative syntax)
var result = db.Orders
    .GroupBy(o => o.CustomerId)
    .Select(g => new {
        CustomerId = g.Key,
        Count = Agg.Count(g),
        Total = Agg.Sum(g, x => x.Amount)
    });
```

### 6. Repository Pattern (FreeSql)

**Add repository support:**
```csharp
public interface IRepository<T> where T : class
{
    IQueryable<T> Query();
    T GetById(object id);
    void Insert(T entity);
    void Update(T entity);
    void Delete(T entity);
    void SaveChanges();
}

public class Repository<T> : IRepository<T> where T : class
{
    private readonly LiteContext _context;
    
    public Repository(LiteContext context)
    {
        _context = context;
    }
    
    public IQueryable<T> Query() => _context.GetTable<T>().AsQueryable();
    
    // ... implement other methods
}
```

---

## 6. Recommended Improvements for LiteSql

### Priority 1: High Impact, Low Effort

1. **WhereIf Extension** ⭐⭐⭐⭐⭐
   - Effort: 1 hour
   - Impact: High (cleaner conditional queries)
   
2. **ToSql() Debug Method** ⭐⭐⭐⭐⭐
   - Effort: 2 hours
   - Impact: High (easier debugging)

3. **Separate IGroupByQuery Interface** ⭐⭐⭐⭐
   - Effort: 4 hours
   - Impact: Medium (cleaner API)

### Priority 2: Medium Impact, Medium Effort

4. **Expression Caching** ⭐⭐⭐⭐
   - Effort: 1 week
   - Impact: High (performance)

5. **Bulk Operations** ⭐⭐⭐⭐
   - Effort: 2 weeks
   - Impact: High (performance for large datasets)

6. **Repository Pattern** ⭐⭐⭐
   - Effort: 1 week
   - Impact: Medium (better architecture)

### Priority 3: High Impact, High Effort

7. **Multi-Database Support** ⭐⭐⭐⭐⭐
   - Effort: 4 weeks
   - Impact: Very High (MySQL, PostgreSQL, Oracle)

8. **Sharding Support** ⭐⭐⭐
   - Effort: 6 weeks
   - Impact: Medium (enterprise scenarios)

---

## 7. Implementation Roadmap

### Phase 35: Quick Wins (1 week)
- WhereIf extension
- ToSql() debug method
- OrderByIf, SelectIf extensions

### Phase 36: API Improvements (2 weeks)
- Separate IGroupByQuery interface
- Better error messages
- Fluent API enhancements

### Phase 37: Performance (3 weeks)
- Expression caching
- Query plan caching
- Bulk operations

### Phase 38: Multi-Database (4 weeks)
- Abstract SQL dialect
- MySQL support
- PostgreSQL support

### Phase 39: Advanced Features (4 weeks)
- Repository pattern
- Unit of Work
- Change tracking

---

## 8. Conclusion

**Best Practices to Adopt:**

1. **From linq2db:**
   - Expression caching
   - Multi-database abstraction
   - Bulk operations

2. **From SqlSugar:**
   - WhereIf pattern
   - Simple, consistent API
   - Easy to learn

3. **From FreeSql:**
   - ISelect separation
   - ToSql() debugging
   - Repository pattern
   - Sharding support

**LiteSql's Unique Strengths:**
- ✅ Lightweight (built on Dapper)
- ✅ Simple codebase
- ✅ Fast learning curve
- ✅ No magic, transparent SQL

**Next Steps:**
1. Implement WhereIf and ToSql() (quick wins)
2. Refactor GroupBy to IGroupByQuery interface
3. Add expression caching
4. Plan multi-database support
