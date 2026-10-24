using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.SqlServer.Tests;

public sealed class SharedEfQueryTranslationTests
{
    [Fact]
    public void EveryTypedFilterAndCorrelatedCursorTranslatesWithoutOpeningAConnection()
    {
        using var db = new SqlServerListDbContext(new DbContextOptionsBuilder<SqlServerListDbContext>()
            .UseSqlServer("Server=invalid;Database=lists;User Id=runtime;Password=unused;TrustServerCertificate=true").Options);
        var records = (IQueryable)typeof(SqlServerListDbContext).GetProperty("Records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;
        var engine = typeof(SqlServerListEngine).Assembly.GetType("CellBridge.Lists.SqlServer.EfListEngine")!;
        foreach (var type in Enum.GetValues<ListFieldType>())
        {
            var field = new ListField(Guid.NewGuid(), "Value", type);
            JsonNode value = type switch
            {
                ListFieldType.Integer => JsonValue.Create(long.MinValue)!,
                ListFieldType.Decimal => JsonValue.Create(-1234567890123456.123456789012m)!,
                ListFieldType.Boolean => JsonValue.Create(false)!,
                ListFieldType.Timestamp => JsonValue.Create("2026-10-24T12:00:00.1234567Z")!,
                ListFieldType.Reference => JsonValue.Create(Guid.NewGuid().ToString("D"))!,
                _ => JsonValue.Create("a \U0001F680 '")!
            };
            foreach (var descending in new[] { false, true })
            foreach (var filter in new[] { new RecordFilter(field.Id, FilterOperator.Equal, value),
                new RecordFilter(field.Id, FilterOperator.NotEqual, value), new RecordFilter(field.Id, FilterOperator.Equal, null) })
            {
                var filtered = Query("ApplyFilter", records, field, filter);
                var anchor = Anchor(filtered, Guid.NewGuid());
                var page = Query("PageAfterSort", filtered, anchor, field, descending, 3);
                var ordered = Query("ApplyPageOrder", page, field, descending);
                var text = ordered.ToQueryString();
                Assert.Contains("TOP", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("ORDER BY", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("scalar_key", text, StringComparison.OrdinalIgnoreCase);
            }
        }
        foreach (var descending in new[] { false, true })
        {
            var page = Query("PageAfterRecordId", records, Anchor(records, Guid.NewGuid()), descending, 3);
            Assert.Contains("TOP", Query("ApplyPageOrder", page, null, descending).ToQueryString(), StringComparison.OrdinalIgnoreCase);
        }

        IQueryable Query(string method, params object?[] args) =>
            (IQueryable)engine.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    }

    [Fact]
    public void CompiledContextClockUsesUtcWithAnExplicitZeroOffset()
    {
        using var db = new SqlServerListDbContext(new DbContextOptionsBuilder<SqlServerListDbContext>()
            .UseSqlServer("Server=invalid;Database=lists;User Id=runtime;Password=unused;TrustServerCertificate=true").Options);
        var sql = db.Records.Select(_ => SqlServerListDbContext.ClockTimestamp()).ToQueryString();
        Assert.Contains("SYSUTCDATETIME", sql);
        Assert.Contains("TODATETIMEOFFSET", sql);
        Assert.Contains("+00:00", sql);
    }

    private static IQueryable Anchor(IQueryable records, Guid id)
    {
        var row = Expression.Parameter(records.ElementType, "row");
        var predicate = Expression.Lambda(Expression.Equal(Expression.Property(row, "RecordId"), Expression.Constant(id)), row);
        return records.Provider.CreateQuery(Expression.Call(typeof(Queryable), nameof(Queryable.Where), [records.ElementType], records.Expression, Expression.Quote(predicate)));
    }
}
