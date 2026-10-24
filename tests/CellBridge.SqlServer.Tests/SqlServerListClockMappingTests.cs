using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.SqlServer.Tests;

public sealed class SqlServerListClockMappingTests
{
    [Fact]
    public void DatabaseClockTranslatesToUtcWithAnExplicitZeroOffset()
    {
        using var db = new SqlServerListDbContext(new DbContextOptionsBuilder<SqlServerListDbContext>()
            .UseSqlServer("Server=invalid;Database=lists;User Id=runtime;Password=unused;TrustServerCertificate=true").Options);
        var sql = db.Records.Select(_ => SqlServerListDbContext.ClockTimestamp()).ToQueryString();
        Assert.Contains("SYSUTCDATETIME", sql);
        Assert.Contains("TODATETIMEOFFSET", sql);
        Assert.Contains("+00:00", sql);
    }
}
