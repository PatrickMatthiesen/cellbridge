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
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
            var normal = new PostgreSqlStateStore(source, limits);
            var state = (await new CellBridgeDocumentService(new(normal, new PostgreSqlContentStore(source), limits))
                .CreateAsync("/cancel-update.docx", MinimalDocx.Create(), TestActor.Value))!;
            using var cancellation = new CancellationTokenSource();
            var interceptor = new CancelUpdate(cancellation);
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .AddInterceptors(interceptor).Options;
            var callbacks = 0;

            var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new PostgreSqlStateStore(options, limits).TransitionAsync(state.ResourceId, (current, now) =>
                {
                    callbacks++;
                    return new StateTransition<bool>(current with { ModifiedUtc = now }, true);
                }, cancellation.Token).AsTask());

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(1, callbacks);
            Assert.Equal(1, interceptor.Updates);
            var unchanged = (await normal.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Equal(state.StateVersion, unchanged.StateVersion);
            Assert.Equal(state.ModifiedUtc, unchanged.ModifiedUtc);
            Assert.Equal(state.Content, unchanged.Content);
            Assert.Equal(state.StateVersion, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await using var command = source.CreateCommand("SELECT count(*) FROM cellbridge_states");
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        });

    private sealed class CancelUpdate(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public int Updates { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("UPDATE cellbridge_documents", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            Updates++;
            cancellation.Cancel();
            throw new OperationCanceledException("Cancelled before commit.",
                new PostgresException("Cancelled update.", "ERROR", "ERROR", "57014"), cancellation.Token);
        }
    }
}
