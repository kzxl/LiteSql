# LiteSql - Implementation Roadmap (High Priority Phases)

**Date:** 2026-04-02  
**Based on:** ORM Research (linq2db, SqlSugar, FreeSql) + Current Status

---

## Current Status Summary

### ✅ Completed Phases
- **Phase 28:** GroupBy Support (100% SQL Server, production ready)
- **Phase 34:** CodeGen File Splitting (foundation complete, CLI pending)
- **Phase 35:** Query Extensions (ToSql working, WhereIf limited)

### 📊 Test Coverage
- Total: 210/223 tests passing (94%)
- SQL Server GroupBy: 16/16 (100%)
- Advanced GroupBy: 11/14 (79%)

---

## High Priority Phases (Next 4-8 Weeks)

### Phase 36: Expression Caching ⭐⭐⭐⭐⭐
**Priority:** CRITICAL  
**Effort:** 1 week  
**ROI:** 10/10 (Performance)  
**Impact:** 30-50% query performance improvement

#### Why Critical?
- Every LINQ query compiles expressions (expensive)
- linq2db shows 40% performance gain with caching
- Low effort, high impact
- No breaking changes

#### Implementation Plan
```csharp
// 1. Create ExpressionCache class
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

// 2. Update WhereBuilder to use cache
var compiled = ExpressionCache.GetOrAdd(predicate);

// 3. Update SelectBuilder to use cache
var compiled = ExpressionCache.GetOrAdd(selector);

// 4. Update GroupByBuilder to use cache
var compiled = ExpressionCache.GetOrAdd(keySelector);
```

#### Files to Modify
1. `src/LiteSql/Sql/ExpressionCache.cs` (NEW - 80 lines)
2. `src/LiteSql/Sql/WhereBuilder.cs` (add caching)
3. `src/LiteSql/Sql/SelectBuilder.cs` (add caching)
4. `src/LiteSql/Sql/GroupByBuilder.cs` (add caching)
5. `tests/LiteSql.Tests/ExpressionCacheTests.cs` (NEW - 200 lines)

#### Success Metrics
- ✅ 30%+ performance improvement on repeated queries
- ✅ No breaking changes
- ✅ All existing tests pass
- ✅ Cache hit rate > 80% in benchmarks

#### Timeline
- Day 1-2: Implement ExpressionCache class
- Day 3-4: Integrate with builders
- Day 5: Performance benchmarks
- Day 6-7: Tests and optimization

---

### Phase 37: Complete CodeGen CLI ⭐⭐⭐⭐
**Priority:** HIGH  
**Effort:** 3 days  
**ROI:** 8/10 (Developer Experience)  
**Impact:** Better IDE performance, fewer merge conflicts

#### Why High Priority?
- Foundation already complete (Phase 34)
- Just need CLI updates
- Immediate value for large databases
- Requested by user

#### Implementation Plan
```bash
# Add command line options
litesql-codegen -c "..." -n MyApp.Models -o Models/ --split
litesql-codegen -c "..." -n MyApp.Models -o MyDb.cs --single-file
```

#### Files to Modify
1. `src/LiteSql.CodeGen/Program.cs` (add CLI options)
2. `tests/LiteSql.CodeGen.Tests/CodeGenTests.cs` (test both modes)
3. `README.md` (update documentation)

#### Timeline
- Day 1: Add CLI options
- Day 2: Tests
- Day 3: Documentation and examples

---

### Phase 38: Bulk Operations ⭐⭐⭐⭐⭐
**Priority:** CRITICAL  
**Effort:** 2 weeks  
**ROI:** 10/10 (Performance)  
**Impact:** 100x faster for large datasets

#### Why Critical?
- Current: InsertOnSubmit loops (slow for 1000+ rows)
- Bulk insert: Single SQL statement
- linq2db and FreeSql both have this
- Essential for enterprise apps

#### Implementation Plan
```csharp
// 1. Add BulkInsert method
public void BulkInsert<T>(IEnumerable<T> entities) where T : class
{
    var mapping = MappingCache.GetMapping<T>();
    var sql = GenerateBulkInsertSql(mapping, entities);
    _connection.Execute(sql, entities);
}

// 2. SQL Server: Use Table-Valued Parameters
INSERT INTO Orders (CustomerId, Amount, Status)
VALUES (@CustomerId1, @Amount1, @Status1),
       (@CustomerId2, @Amount2, @Status2),
       ...

// 3. SQLite: Use multiple INSERT
INSERT INTO Orders (CustomerId, Amount, Status)
SELECT @CustomerId1, @Amount1, @Status1
UNION ALL SELECT @CustomerId2, @Amount2, @Status2
...
```

#### Files to Create
1. `src/LiteSql/BulkOperations.cs` (NEW - 300 lines)
2. `src/LiteSql/Sql/BulkInsertBuilder.cs` (NEW - 200 lines)
3. `tests/LiteSql.Tests/BulkOperationsTests.cs` (NEW - 400 lines)

#### Success Metrics
- ✅ 100x faster than loop for 1000 rows
- ✅ Support SQL Server and SQLite
- ✅ Handle 10,000+ rows efficiently
- ✅ Proper error handling

#### Timeline
- Week 1: Implement BulkInsert
- Week 2: BulkUpdate, BulkDelete, tests

---

### Phase 39: Join Support ⭐⭐⭐⭐
**Priority:** HIGH  
**Effort:** 2 weeks  
**ROI:** 9/10 (Feature Completeness)  
**Impact:** Eliminate 60% of raw SQL needs

#### Why High Priority?
- Most requested feature after GroupBy
- Essential for complex queries
- Completes LINQ feature set

#### Implementation Plan
```csharp
// Target API
var result = db.Orders
    .Join(db.Customers, 
        o => o.CustomerId, 
        c => c.Id,
        (o, c) => new { o.Amount, c.Name })
    .Where(x => x.Amount > 100)
    .ToList();

// Generated SQL
SELECT o.[Amount], c.[Name]
FROM [Orders] o
INNER JOIN [Customers] c ON o.[CustomerId] = c.[Id]
WHERE o.[Amount] > @p0
```

#### Files to Create
1. `src/LiteSql/JoinQuery.cs` (NEW - 300 lines)
2. `src/LiteSql/Sql/JoinBuilder.cs` (NEW - 400 lines)
3. `tests/LiteSql.Tests/JoinTests.cs` (NEW - 500 lines)

#### Timeline
- Week 1: Implement INNER JOIN
- Week 2: LEFT JOIN, tests, optimization

---

### Phase 40: Multi-Database Support ⭐⭐⭐⭐⭐
**Priority:** CRITICAL (Long-term)  
**Effort:** 4 weeks  
**ROI:** 10/10 (Market Expansion)  
**Impact:** Support MySQL, PostgreSQL, Oracle

#### Why Critical?
- Expand user base significantly
- Competitive with EF Core
- linq2db, SqlSugar, FreeSql all support this

#### Implementation Plan
```csharp
// 1. Abstract SQL dialect
public interface ISqlDialect
{
    string QuoteIdentifier(string name);
    string GetLimitClause(int? skip, int? take);
    string GetAutoIncrementSql();
}

// 2. Implement dialects
public class SqlServerDialect : ISqlDialect { }
public class MySqlDialect : ISqlDialect { }
public class PostgreSqlDialect : ISqlDialect { }

// 3. Update builders to use dialect
var sql = _dialect.QuoteIdentifier(columnName);
```

#### Files to Create
1. `src/LiteSql/Dialects/ISqlDialect.cs` (NEW - 50 lines)
2. `src/LiteSql/Dialects/SqlServerDialect.cs` (NEW - 100 lines)
3. `src/LiteSql/Dialects/MySqlDialect.cs` (NEW - 100 lines)
4. `src/LiteSql/Dialects/PostgreSqlDialect.cs` (NEW - 100 lines)
5. Update all builders to use dialect

#### Timeline
- Week 1: Design dialect abstraction
- Week 2: Implement SQL Server and MySQL
- Week 3: Implement PostgreSQL
- Week 4: Tests and optimization

---

## Recommended Execution Order

### Sprint 1 (Week 1): Quick Wins
1. **Phase 37:** Complete CodeGen CLI (3 days) ✅
2. **Phase 36:** Expression Caching (4 days) ✅

**Value:** Immediate performance boost + better DX

### Sprint 2 (Weeks 2-3): Performance
3. **Phase 38:** Bulk Operations (2 weeks) ✅

**Value:** 100x faster for large datasets

### Sprint 3 (Weeks 4-5): Feature Completeness
4. **Phase 39:** Join Support (2 weeks) ✅

**Value:** Eliminate most raw SQL needs

### Sprint 4 (Weeks 6-9): Market Expansion
5. **Phase 40:** Multi-Database Support (4 weeks) ✅

**Value:** Support MySQL, PostgreSQL

---

## Alternative: Focus on Stability

If you prefer stability over new features:

### Sprint 1 (Week 1): Polish
1. Fix 3 remaining GroupBy edge cases
2. Complete CodeGen CLI
3. Update documentation

### Sprint 2 (Week 2): Performance
4. Expression caching
5. Query plan caching
6. Benchmarks

### Sprint 3 (Week 3): Testing
7. Increase test coverage to 98%
8. Add integration tests
9. Performance regression tests

---

## Success Metrics (8 Weeks)

### Performance
- ✅ 30-50% faster queries (expression caching)
- ✅ 100x faster bulk operations
- ✅ Query plan caching

### Features
- ✅ Join support (INNER, LEFT)
- ✅ Bulk operations (Insert, Update, Delete)
- ✅ Multi-database (MySQL, PostgreSQL)

### Developer Experience
- ✅ CodeGen split files
- ✅ ToSql() debugging
- ✅ Better error messages

### Quality
- ✅ 98% test coverage
- ✅ Zero critical bugs
- ✅ Comprehensive documentation

---

## Risk Assessment

### Low Risk (Do First)
- ✅ Expression caching (no breaking changes)
- ✅ CodeGen CLI (isolated feature)
- ✅ ToSql() debugging (already done)

### Medium Risk
- ⚠️ Bulk operations (new API surface)
- ⚠️ Join support (complex SQL generation)

### High Risk (Plan Carefully)
- ⚠️ Multi-database (major refactoring)
- ⚠️ Breaking changes to Table<T> API

---

## Recommendation

**Start with Sprint 1 (Quick Wins):**
1. Complete CodeGen CLI (3 days)
2. Expression Caching (4 days)

**Then assess:**
- If performance is priority → Sprint 2 (Bulk Operations)
- If features are priority → Sprint 3 (Join Support)
- If market expansion → Sprint 4 (Multi-DB)

**Total Timeline:** 8 weeks for all high-priority phases

---

**Next Action:** Which sprint should we start with?
1. Sprint 1 (Quick Wins) - Recommended ⭐
2. Sprint 2 (Performance)
3. Sprint 3 (Features)
4. Alternative (Stability)
