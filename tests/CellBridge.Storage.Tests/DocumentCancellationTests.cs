using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class DocumentCancellationTests
{
    [PostgreSqlFact]
    public Task CancellationWithAnInnerDatabaseErrorRollsBackWithoutBecomingAnUnknownOutcome() =>
        RepositoryTestDatabase.WithDatabase(new() { MaxRetainedStateSnapshots = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
            IDocumentRepository normal = new PostgreSqlStateStore(source, limits);
            var state = (await new CellBridgeDocumentService(new(normal, new PostgreSqlContentStore(source), limits))
                .CreateAsync("/cancel-update.docx", MinimalDocx.Create(), TestActor.Value))!;
            using var cancellation = new CancellationTokenSource();
            var interceptor = new CancelUpdate(cancellation);
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .AddInterceptors(interceptor).Options;
            IDocumentRepository intercepted = new PostgreSqlStateStore(options, limits);
            var command = new DocumentControlCommand.SetHostLease(state.ResourceId, TestStorage.Admit(state),
                new HostLease("cancelled", TestActor.Value.Identity.Subject, DateTime.UtcNow.AddMinutes(1)), null);
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                intercepted.ExecuteControlAsync(command, cancellation.Token).AsTask());

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(1, interceptor.Updates);
            var unchanged = (await normal.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
            Assert.Equal(state.StateVersion, unchanged.StateVersion);
            Assert.Equal(state.ModifiedUtc, unchanged.ModifiedUtc);
            Assert.Equal(state.Package, unchanged.Package);
            var control = (await normal.ReadControlAsync(state.ResourceId))!;
            Assert.Equal(state.StateVersion, control.Head.StateVersion);
            Assert.Null(control.Coordination.HostLock);
            Assert.Equal(state.Counters, unchanged.Counters);
            await using var receipts = source.CreateCommand("SELECT count(*) FROM cellbridge_repository_receipts");
            Assert.Equal(0L, (long)(await receipts.ExecuteScalarAsync())!);
        });

    private sealed class CancelUpdate(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public int Updates { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("UPDATE cellbridge_repository_heads", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            Updates++;
            cancellation.Cancel();
            throw new OperationCanceledException("Cancelled before commit.",
                new PostgresException("Cancelled update.", "ERROR", "ERROR", "57014"), cancellation.Token);
        }
    }
}
