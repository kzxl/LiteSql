# Phase 28: Advanced Test Cases & Architecture Enhancement

## Additional Test Cases to Cover

### 1. Edge Cases & Error Handling

#### NULL Value Handling
```csharp
[Fact]
public void GroupBy_WithNullValues()
{
    // Test data with NULL amounts
    var result = orders
        .GroupBy(o => o.CustomerId)
        .Select(g => new {
            CustomerId = g.Key,
            TotalAmount = g.Sum(x => x.Amount),  // Should handle NULL
            AvgAmount = g.Average(x => x.Amount),
            MinAmount = g.Min(x => x.Amount),
            MaxAmount = g.Max(x => x.Amount)
        });
}

[Fact]
public void GroupBy_WithNullableGroupKey()
{
    // GroupBy on nullable column
    var result = orders
        .GroupBy(o => o.CategoryId)  // CategoryId is nullable
        .Select(g => new { CategoryId = g.Key, Count = g.Count() });
}
```

#### Empty Result Sets
```csharp
[Fact]
public void GroupBy_OnEmptyTable()
{
    // Should return empty list, not throw
    var result = emptyOrders
        .GroupBy(o => o.CustomerId)
        .Select(g => new { g.Key, Count = g.Count() });
    
    Assert.Empty(result);
}
```

#### Large Data Sets
```csharp
[Fact]
public void GroupBy_WithLargeDataSet()
{
    // Test with 100K+ rows
    // Verify performance and memory usage
    var result = largeOrders
        .GroupBy(o => o.CustomerId)
        .Select(g => new {
            CustomerId = g.Key,
            Count = g.Count(),
            Total = g.Sum(x => x.Amount)
        });
}
```

### 2. Complex Aggregation Scenarios

#### Nested Aggregates
```csharp
[Fact]
public void GroupBy_WithConditionalAggregates()
{
    // Count with condition (similar to COUNT(CASE WHEN...))
    var result = orders
        .GroupBy(o => o.CustomerId)
        .Select(g => new {
            CustomerId = g.Key,
            TotalOrders = g.Count(),
            CompletedOrders = g.Count(x => x.Status == "Completed"),
            TotalAmount = g.Sum(x => x.Amount),
            CompletedAmount = g.Sum(x => x.Status == "Completed" ? x.Amount : 0)
        });
}
```

#### Multiple GroupBy Levels
```csharp
[Fact]
public void GroupBy_MultiLevel()
{
    // First group by customer, then by year
    var result = orders
        .GroupBy(o => new { o.CustomerId, o.Year })
        .Select(g => new {
            g.Key.CustomerId,
            g.Key.Year,
            Count = g.Count(),
            Total = g.Sum(x => x.Amount)
        })
        .GroupBy(x => x.CustomerId)  // Second level grouping
        .Select(g => new {
            CustomerId = g.Key,
            YearCount = g.Count(),
            TotalAmount = g.Sum(x => x.Total)
        });
}
```

#### DISTINCT in Aggregates
```csharp
[Fact]
public void GroupBy_WithDistinctCount()
{
    // COUNT(DISTINCT column)
    var result = orders
        .GroupBy(o => o.CustomerId)
        .Select(g => new {
            CustomerId = g.Key,
            UniqueProducts = g.Select(x => x.ProductId).Distinct().Count()
        });
}
```

### 3. Performance & Optimization Tests

#### Index Usage Verification
```csharp
[Fact]
public void GroupBy_UsesIndexes()
{
    // Verify that GROUP BY uses indexes
    // Check execution plan
    var result = orders
        .GroupBy(o => o.CustomerId)  // CustomerId should be indexed
        .Select(g => new { g.Key, Count = g.Count() });
    
    // Assert execution plan shows index seek, not scan
}
```

#### Query Caching
```csharp
[Fact]
public void GroupBy_QueryCaching()
{
    // First execution
    var result1 = orders.GroupBy(o => o.CustomerId).Select(g => new { g.Key, Count = g.Count() });
    
    // Second execution - should use cached query plan
    var result2 = orders.GroupBy(o => o.CustomerId).Select(g => new { g.Key, Count = g.Count() });
    
    // Verify same SQL generated
}
```

### 4. Integration with Other LINQ Operations

#### GroupBy + Join
```csharp
[Fact]
public void GroupBy_WithJoin()
{
    var result = orders
        .Join(customers, o => o.CustomerId, c => c.Id, (o, c) => new { o, c })
        .GroupBy(x => x.c.Country)
        .Select(g => new {
            Country = g.Key,
            TotalOrders = g.Count(),
            TotalAmount = g.Sum(x => x.o.Amount)
        });
}
```

#### GroupBy + Include (Navigation Properties)
```csharp
[Fact]
public void GroupBy_WithNavigationProperties()
{
    var result = orders
        .Include(o => o.Customer)
        .GroupBy(o => o.Customer.Country)
        .Select(g => new {
            Country = g.Key,
            Count = g.Count()
        });
}
```

#### GroupBy + Pagination
```csharp
[Fact]
public void GroupBy_WithPagination()
{
    var result = orders
        .GroupBy(o => o.CustomerId)
        .Select(g => new {
            CustomerId = g.Key,
            Total = g.Sum(x => x.Amount)
        })
        .OrderByDescending(x => x.Total)
        .Skip(10)
        .Take(10);
}
```

### 5. SQL Server Specific Features

#### ROLLUP
```csharp
[Fact]
public void GroupBy_WithRollup()
{
    // GROUP BY CustomerId, Year WITH ROLLUP
    var result = orders
        .GroupBy(o => new { o.CustomerId, o.Year })
        .WithRollup()  // Extension method
        .Select(g => new {
            CustomerId = g.Key.CustomerId,
            Year = g.Key.Year,
            Total = g.Sum(x => x.Amount)
        });
}
```

#### CUBE
```csharp
[Fact]
public void GroupBy_WithCube()
{
    // GROUP BY CustomerId, Year WITH CUBE
    var result = orders
        .GroupBy(o => new { o.CustomerId, o.Year })
        .WithCube()  // Extension method
        .Select(g => new {
            CustomerId = g.Key.CustomerId,
            Year = g.Key.Year,
            Total = g.Sum(x => x.Amount)
        });
}
```

#### GROUPING SETS
```csharp
[Fact]
public void GroupBy_WithGroupingSets()
{
    // GROUP BY GROUPING SETS ((CustomerId), (Year), ())
    var result = orders
        .GroupBy(o => new { o.CustomerId, o.Year })
        .WithGroupingSets(
            new[] { "CustomerId" },
            new[] { "Year" },
            new string[] { }
        )
        .Select(g => new {
            CustomerId = g.Key.CustomerId,
            Year = g.Key.Year,
            Total = g.Sum(x => x.Amount)
        });
}
```

### 6. Error Scenarios

#### Invalid Expressions
```csharp
[Fact]
public void GroupBy_ThrowsOnInvalidExpression()
{
    // Should throw meaningful error
    Assert.Throws<NotSupportedException>(() =>
        orders.GroupBy(o => o.CustomerId + o.Year)  // Complex expression not supported
    );
}

[Fact]
public void GroupBy_ThrowsOnNonAggregateInSelect()
{
    // Should throw when selecting non-grouped, non-aggregated column
    Assert.Throws<InvalidOperationException>(() =>
        orders
            .GroupBy(o => o.CustomerId)
            .Select(g => new { g.Key, g.First().Amount })  // Amount not aggregated
    );
}
```

## Universe Architect Pattern Application

### Current Architecture (Monolithic)
```
Table<T> → GroupByQuery<T, TKey> → GroupByBuilder → SQL
```

### Universe Architect Pattern (Modular & Extensible)

#### 1. Core Universe (Stable Foundation)
```csharp
// Core abstractions that rarely change
public interface IQueryBuilder
{
    string BuildQuery();
    IDictionary<string, object> GetParameters();
}

public interface IAggregateFunction
{
    string ToSql();
    Type ReturnType { get; }
}

public interface IGroupingStrategy
{
    string BuildGroupByClause();
    string BuildHavingClause();
}
```

#### 2. Planet Layer (Feature Modules)
```csharp
// Each "planet" is an independent feature module

// Planet: Aggregates
public class AggregatesPlanet
{
    public class CountFunction : IAggregateFunction { }
    public class SumFunction : IAggregateFunction { }
    public class AvgFunction : IAggregateFunction { }
    public class MinFunction : IAggregateFunction { }
    public class MaxFunction : IAggregateFunction { }
    public class DistinctCountFunction : IAggregateFunction { }
}

// Planet: Advanced Grouping
public class AdvancedGroupingPlanet
{
    public class RollupStrategy : IGroupingStrategy { }
    public class CubeStrategy : IGroupingStrategy { }
    public class GroupingSetsStrategy : IGroupingStrategy { }
}

// Planet: Optimization
public class OptimizationPlanet
{
    public class QueryCache { }
    public class IndexHintProvider { }
    public class ExecutionPlanAnalyzer { }
}
```

#### 3. Satellite Layer (Extensions)
```csharp
// Satellites orbit planets and provide specialized functionality

// Satellite: SQL Server Extensions
public class SqlServerSatellite
{
    public static GroupByQuery<T, TKey> WithRollup<T, TKey>(
        this GroupByQuery<T, TKey> query)
    {
        query.SetGroupingStrategy(new RollupStrategy());
        return query;
    }
}

// Satellite: Performance Monitoring
public class PerformanceMonitoringSatellite
{
    public static GroupByQuery<T, TKey> WithProfiling<T, TKey>(
        this GroupByQuery<T, TKey> query)
    {
        query.EnableProfiling();
        return query;
    }
}
```

#### 4. Galaxy Layer (Cross-Cutting Concerns)
```csharp
// Galaxy: Logging & Diagnostics
public class DiagnosticsGalaxy
{
    public static void LogQuery(string sql, IDictionary<string, object> parameters);
    public static void LogExecutionTime(TimeSpan duration);
    public static void LogExecutionPlan(string plan);
}

// Galaxy: Security
public class SecurityGalaxy
{
    public static void ValidateQuery(string sql);
    public static void SanitizeParameters(IDictionary<string, object> parameters);
}
```

### Refactored Architecture

```csharp
// Core Universe
public class GroupByQueryBuilder : IQueryBuilder
{
    private readonly IGroupingStrategy _groupingStrategy;
    private readonly List<IAggregateFunction> _aggregates;
    private readonly IOptimizationStrategy _optimization;
    
    public GroupByQueryBuilder(IGroupingStrategy groupingStrategy)
    {
        _groupingStrategy = groupingStrategy;
        _aggregates = new List<IAggregateFunction>();
    }
    
    public GroupByQueryBuilder WithAggregate(IAggregateFunction aggregate)
    {
        _aggregates.Add(aggregate);
        return this;
    }
    
    public GroupByQueryBuilder WithOptimization(IOptimizationStrategy optimization)
    {
        _optimization = optimization;
        return this;
    }
    
    public string BuildQuery()
    {
        var sql = new StringBuilder();
        sql.Append("SELECT ");
        sql.Append(_groupingStrategy.BuildSelectClause());
        sql.Append(", ");
        sql.Append(string.Join(", ", _aggregates.Select(a => a.ToSql())));
        sql.Append(" FROM ");
        sql.Append(_tableName);
        sql.Append(" GROUP BY ");
        sql.Append(_groupingStrategy.BuildGroupByClause());
        
        if (_havingClause != null)
        {
            sql.Append(" HAVING ");
            sql.Append(_groupingStrategy.BuildHavingClause());
        }
        
        if (_optimization != null)
        {
            sql.Append(" ");
            sql.Append(_optimization.GetHints());
        }
        
        return sql.ToString();
    }
}

// Usage with Universe Architect
var query = new GroupByQueryBuilder(new StandardGroupingStrategy())
    .WithAggregate(new CountFunction())
    .WithAggregate(new SumFunction("Amount"))
    .WithOptimization(new IndexHintOptimization("IX_CustomerId"))
    .WithRollup()  // Satellite extension
    .WithProfiling();  // Satellite extension

var sql = query.BuildQuery();
```

### Benefits of Universe Architect Pattern

1. **Modularity**: Each planet is independent and can be developed/tested separately
2. **Extensibility**: New features added as satellites without touching core
3. **Maintainability**: Clear separation of concerns
4. **Testability**: Each component can be unit tested in isolation
5. **Scalability**: New databases/features added as new planets
6. **Flexibility**: Mix and match components as needed

### Migration Path

**Phase 1: Extract Interfaces (No Breaking Changes)**
```csharp
// Add interfaces alongside existing code
public interface IAggregateFunction { }
public class CountAggregate : IAggregateFunction { }

// Existing code continues to work
```

**Phase 2: Implement Planets (Parallel Development)**
```csharp
// Build new planet modules
public class AggregatesPlanet { }
public class OptimizationPlanet { }

// Old code still works, new code uses planets
```

**Phase 3: Add Satellites (Gradual Enhancement)**
```csharp
// Add extension methods
public static class SqlServerExtensions
{
    public static GroupByQuery<T, TKey> WithRollup<T, TKey>(...)
}
```

**Phase 4: Deprecate Old API (Optional)**
```csharp
// Mark old methods as obsolete
[Obsolete("Use WithAggregate() instead")]
public GroupByQuery<T, TKey> Sum(...) { }
```

## Recommended Implementation Priority

### High Priority (Phase 28.1)
1. ✅ NULL value handling
2. ✅ Empty result sets
3. ✅ Error scenarios with clear messages
4. ✅ Integration with Join (Phase 29 dependency)

### Medium Priority (Phase 28.2)
1. Conditional aggregates (COUNT with WHERE)
2. DISTINCT in aggregates
3. Query caching
4. Performance profiling

### Low Priority (Phase 28.3)
1. ROLLUP/CUBE/GROUPING SETS (SQL Server specific)
2. Multi-level GroupBy
3. Universe Architect refactoring

### Future (Phase 29+)
1. Subqueries in HAVING
2. Window functions
3. Recursive CTEs
