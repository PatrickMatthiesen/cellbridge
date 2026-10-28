using System.Data;
using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerPreparationReadTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task PreparationRetainsSerializableProtectionAndProviderClock()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var initial = (await new CellBridgeDocumentService(provider)
            .CreateAsync("/preparation.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var audit = new PreparationAudit();
        var reader = new SqlServerStateStore(database.Options(false, audit));
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        var before = await admin.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync();
        var view = (await reader.ReadPreparationAsync(initial.ResourceId))!;
        var after = await admin.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync();
        Assert.Equal(initial.StateVersion, view.State.StateVersion);
        Assert.Equal(initial.Content, view.State.Content);
        Assert.Equal(DateTimeKind.Utc, view.UtcNow.Kind);
        Assert.InRange(view.UtcNow, DateTime.SpecifyKind(before, DateTimeKind.Utc), DateTime.SpecifyKind(after, DateTimeKind.Utc));
        Assert.Equal(1, audit.Reads);
        Assert.Equal(IsolationLevel.Serializable, audit.Isolation);
        Assert.Null(await reader.ReadPreparationAsync(Guid.NewGuid()));

        await admin.Snapshots.Where(row => row.ResourceId == initial.ResourceId).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<StorageCorruptionException>(() => reader.ReadPreparationAsync(initial.ResourceId).AsTask());
    }

    private sealed class PreparationAudit : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public IsolationLevel Isolation { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SYSUTCDATETIME", StringComparison.OrdinalIgnoreCase))
            {
                Reads++;
                Assert.NotNull(command.Transaction);
                Isolation = command.Transaction.IsolationLevel;
            }
            return ValueTask.FromResult(result);
        }
    }
}
